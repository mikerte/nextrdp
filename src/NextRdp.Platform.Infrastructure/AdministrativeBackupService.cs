using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Infrastructure;

public sealed record BackupEncryptionOptions(int Pbkdf2Iterations = 600000)
{
    public void Validate()
    {
        if (Pbkdf2Iterations is < 100000 or > 5000000)
            throw new InvalidOperationException("Backup PBKDF2 iterations must be between 100,000 and 5,000,000.");
    }
}

public sealed class AdministrativeBackupService : IAdministrativeBackupService
{
    private const int SchemaVersion = 3;
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int HeaderLength = 8 + sizeof(int) + sizeof(int) + SaltLength + NonceLength + TagLength;
    private const int MaximumArtifactBytes = 100 * 1024 * 1024;
    private static readonly byte[] Magic = "NRDPBKP2"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PlatformDbContext dbContext;
    private readonly IResourceAuthorizationService resourceAuthorization;
    private readonly IAttributePolicyService attributePolicy;
    private readonly TimeProvider timeProvider;
    private readonly BackupEncryptionOptions options;
    private readonly IResourceRelationshipService relationships;

    public AdministrativeBackupService(
        PlatformDbContext dbContext,
        IResourceAuthorizationService resourceAuthorization,
        IAttributePolicyService attributePolicy,
        TimeProvider timeProvider,
        BackupEncryptionOptions options,
        IResourceRelationshipService relationships)
    {
        this.dbContext = dbContext;
        this.resourceAuthorization = resourceAuthorization;
        this.attributePolicy = attributePolicy;
        this.timeProvider = timeProvider;
        this.options = options;
        this.relationships = relationships;
        options.Validate();
    }

    public async Task<BackupArtifact> CreateAsync(PlatformActor actor, Guid organizationId, BackupRequest request, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.CreateBackup, new AuthorizationTarget(organizationId, null, "organization", organizationId), cancellationToken);
        ValidatePassphrase(request.Passphrase);
        var organization = await dbContext.Organizations.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == organizationId, cancellationToken)
            ?? throw PlatformProblemException.NotFound("organization");
        var payload = new BackupPayload(
            SchemaVersion,
            timeProvider.GetUtcNow(),
            organization,
            await dbContext.Workspaces.AsNoTracking().Where(entity => entity.OrganizationId == organizationId).ToArrayAsync(cancellationToken),
            await UsersInOrganizationAsync(organizationId, cancellationToken),
            await dbContext.Memberships.AsNoTracking().Where(entity => entity.OrganizationId == organizationId).ToArrayAsync(cancellationToken),
            await dbContext.GroupRoleMappings.AsNoTracking().Where(entity => entity.OrganizationId == organizationId).ToArrayAsync(cancellationToken),
            await dbContext.Folders.AsNoTracking().Where(entity => entity.OrganizationId == organizationId).ToArrayAsync(cancellationToken),
            await dbContext.Connections.AsNoTracking().Where(entity => entity.OrganizationId == organizationId).ToArrayAsync(cancellationToken),
            await dbContext.DynamicGroups.AsNoTracking().Where(entity => entity.OrganizationId == organizationId).ToArrayAsync(cancellationToken),
            await dbContext.ConnectionTemplates.AsNoTracking().Where(entity => entity.OrganizationId == organizationId).ToArrayAsync(cancellationToken),
            await dbContext.UserWorkspaceStates.AsNoTracking().Where(entity => entity.OrganizationId == organizationId).ToArrayAsync(cancellationToken),
            await dbContext.ConfigurationDrafts.AsNoTracking().Where(entity => entity.OrganizationId == organizationId).ToArrayAsync(cancellationToken),
            await dbContext.PublishedConfigurations.AsNoTracking().Where(entity => entity.OrganizationId == organizationId).ToArrayAsync(cancellationToken),
            await dbContext.ServiceAccounts.AsNoTracking().Where(entity => entity.OrganizationId == organizationId).ToArrayAsync(cancellationToken),
            await dbContext.SyncChanges.AsNoTracking().Where(entity => entity.OrganizationId == organizationId).OrderBy(entity => entity.Sequence).ToArrayAsync(cancellationToken),
            request.IncludeAudit ? await dbContext.AuditEvents.AsNoTracking().Where(entity => entity.OrganizationId == organizationId).OrderBy(entity => entity.OccurredAtUtc).ToArrayAsync(cancellationToken) : []);
        ValidatePayload(payload);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (plaintext.Length > MaximumArtifactBytes - HeaderLength) throw new PlatformProblemException(413, "backup_too_large", "Backup too large", "The administrative backup exceeds the 100 MiB safety limit.");
        var artifact = Encrypt(plaintext, request.Passphrase, options.Pbkdf2Iterations);
        CryptographicOperations.ZeroMemory(plaintext);
        var hash = Convert.ToHexString(SHA256.HashData(artifact));
        var fileName = $"nextrdp-{organization.Slug}-{timeProvider.GetUtcNow():yyyyMMddHHmmss}.nextrdp-backup";

        dbContext.AuditEvents.Add(new PlatformAuditEvent
        {
            OrganizationId = organizationId,
            ActorUserId = actor.IsServiceAccount ? null : actor.PrincipalId,
            ServiceAccountId = actor.IsServiceAccount ? actor.PrincipalId : null,
            Action = "administrative_backup.created",
            ResourceType = "organization",
            ResourceId = organizationId,
            Outcome = AuditOutcome.Succeeded,
            RedactedDetailJson = JsonSerializer.Serialize(new { request.IncludeAudit, schemaVersion = SchemaVersion, artifactBytes = artifact.Length }, JsonOptions),
            OccurredAtUtc = timeProvider.GetUtcNow()
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return new BackupArtifact(fileName, artifact, hash);
    }

    public async Task<RestoreResult> RestoreAsync(PlatformActor actor, ReadOnlyMemory<byte> content, string passphrase, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.RestoreBackup, new AuthorizationTarget(Guid.Empty, null, "platform", Guid.Empty), cancellationToken);
        ValidatePassphrase(passphrase);
        if (content.Length is < HeaderLength or > MaximumArtifactBytes) throw PlatformProblemException.Validation(["Backup artifact size is invalid."]);
        if (await dbContext.Organizations.AnyAsync(cancellationToken))
            throw PlatformProblemException.Conflict("Administrative restore requires a clean NextRDP platform database.");

        var plaintext = Decrypt(content.Span, passphrase);
        BackupPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<BackupPayload>(plaintext, JsonOptions)
                ?? throw PlatformProblemException.Validation(["Backup payload is empty."]);
        }
        catch (JsonException)
        {
            throw PlatformProblemException.Validation(["Backup payload is malformed."]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
        ValidatePayload(payload);

        var usersToRestore = payload.Users.ToList();
        var bootstrapUsers = await dbContext.Users.ToListAsync(cancellationToken);
        foreach (var bootstrap in bootstrapUsers)
        {
            var restored = usersToRestore.SingleOrDefault(user => user.Issuer == bootstrap.Issuer && user.Subject == bootstrap.Subject);
            if (restored is null) continue;
            RemapUserReferences(payload, restored.Id, bootstrap.Id);
            bootstrap.DisplayName = restored.DisplayName;
            bootstrap.EmailHint = restored.EmailHint;
            bootstrap.Status = restored.Status;
            bootstrap.LastAuthenticatedAtUtc = restored.LastAuthenticatedAtUtc;
            bootstrap.SessionVersion = Math.Max(bootstrap.SessionVersion, restored.SessionVersion) + 1;
            usersToRestore.Remove(restored);
        }

        var executionStrategy = dbContext.Database.CreateExecutionStrategy();
        await executionStrategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            dbContext.Organizations.Add(payload.Organization);
            dbContext.Workspaces.AddRange(payload.Workspaces);
            dbContext.Users.AddRange(usersToRestore);
            dbContext.Memberships.AddRange(payload.Memberships);
            dbContext.GroupRoleMappings.AddRange(payload.GroupRoleMappings);
            dbContext.Folders.AddRange(payload.Folders);
            dbContext.Connections.AddRange(payload.Connections);
            dbContext.DynamicGroups.AddRange(payload.DynamicGroups);
            dbContext.ConnectionTemplates.AddRange(payload.ConnectionTemplates);
            dbContext.UserWorkspaceStates.AddRange(payload.UserWorkspaceStates);
            dbContext.ConfigurationDrafts.AddRange(payload.ConfigurationDrafts);
            dbContext.PublishedConfigurations.AddRange(payload.PublishedConfigurations);
            foreach (var account in payload.ServiceAccounts) account.RevokedAtUtc = timeProvider.GetUtcNow();
            dbContext.ServiceAccounts.AddRange(payload.ServiceAccounts);
            foreach (var change in payload.SyncChanges) change.Sequence = 0;
            dbContext.SyncChanges.AddRange(payload.SyncChanges);
            dbContext.AuditEvents.AddRange(payload.AuditEvents);
            dbContext.AuditEvents.Add(new PlatformAuditEvent
            {
                OrganizationId = payload.Organization.Id,
                ActorUserId = actor.IsServiceAccount ? null : actor.PrincipalId,
                ServiceAccountId = actor.IsServiceAccount ? actor.PrincipalId : null,
                Action = "administrative_backup.restored",
                ResourceType = "organization",
                ResourceId = payload.Organization.Id,
                Outcome = AuditOutcome.Succeeded,
                RedactedDetailJson = JsonSerializer.Serialize(new { schemaVersion = payload.SchemaVersion, serviceAccountsRevoked = payload.ServiceAccounts.Length }, JsonOptions),
                OccurredAtUtc = timeProvider.GetUtcNow()
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            foreach (var membership in payload.Memberships.Where(item => item.Status == MembershipStatus.Active))
            {
                await relationships.EnsureRoleAsync(membership.UserId, false, membership.Role, membership.WorkspaceId is null
                    ? new AuthorizationTarget(membership.OrganizationId, null, "organization", membership.OrganizationId)
                    : new AuthorizationTarget(membership.OrganizationId, membership.WorkspaceId, "workspace", membership.WorkspaceId.Value), cancellationToken);
                if (membership.WorkspaceId is null)
                {
                    foreach (var workspace in payload.Workspaces)
                        await relationships.EnsureRoleAsync(membership.UserId, false, membership.Role,
                            new AuthorizationTarget(membership.OrganizationId, workspace.Id, "workspace", workspace.Id), cancellationToken);
                }
            }
            await transaction.CommitAsync(cancellationToken);
        });

        return new RestoreResult(1, payload.Workspaces.Length, payload.Folders.Length, payload.Connections.Length,
            payload.ConfigurationDrafts.Length + payload.PublishedConfigurations.Length, true);
    }

    private static void RemapUserReferences(BackupPayload payload, Guid oldUserId, Guid newUserId)
    {
        foreach (var membership in payload.Memberships.Where(item => item.UserId == oldUserId)) membership.UserId = newUserId;
        foreach (var connection in payload.Connections.Where(item => item.OwnerUserId == oldUserId)) connection.OwnerUserId = newUserId;
        foreach (var state in payload.UserWorkspaceStates.Where(item => item.UserId == oldUserId)) state.UserId = newUserId;
        foreach (var draft in payload.ConfigurationDrafts.Where(item => item.CreatedByUserId == oldUserId)) draft.CreatedByUserId = newUserId;
        foreach (var published in payload.PublishedConfigurations.Where(item => item.PublishedByUserId == oldUserId)) published.PublishedByUserId = newUserId;
        foreach (var account in payload.ServiceAccounts.Where(item => item.CreatedByUserId == oldUserId)) account.CreatedByUserId = newUserId;
        foreach (var audit in payload.AuditEvents.Where(item => item.ActorUserId == oldUserId)) audit.ActorUserId = newUserId;
        foreach (var change in payload.SyncChanges)
        {
            if (change.AudienceUserId == oldUserId) change.AudienceUserId = newUserId;
            change.PayloadJson = change.PayloadJson
                .Replace(oldUserId.ToString("D"), newUserId.ToString("D"), StringComparison.OrdinalIgnoreCase)
                .Replace(oldUserId.ToString("N"), newUserId.ToString("N"), StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task DemandAsync(PlatformActor actor, PlatformAction action, AuthorizationTarget target, CancellationToken cancellationToken)
    {
        if (!actor.MfaSatisfied) throw PlatformProblemException.Forbidden("The identity provider must confirm MFA before administrative backup operations.");
        var organizationId = target.OrganizationId == Guid.Empty ? actor.OrganizationId ?? Guid.Empty : target.OrganizationId;
        var fixedDecision = target.ResourceType == "platform" && actor.Roles.Contains(WorkspaceRole.PlatformAdministrator)
            ? AuthorizationDecision.Allow("Platform administrator.")
            : FixedRoleAuthorizer.Authorize(actor, action, organizationId);
        if (!fixedDecision.Allowed) throw PlatformProblemException.Forbidden(fixedDecision.Reason);
        var resourceDecision = await resourceAuthorization.AuthorizeAsync(actor, action, target, cancellationToken);
        if (!resourceDecision.Allowed) throw PlatformProblemException.Forbidden(resourceDecision.Reason);
        var policyDecision = await attributePolicy.EvaluateAsync(actor, action, target, new Dictionary<string, object?>(), cancellationToken);
        if (!policyDecision.Allowed) throw PlatformProblemException.Forbidden(policyDecision.Reason);
    }

    private async Task<PlatformUser[]> UsersInOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var userIds = dbContext.Memberships.Where(entity => entity.OrganizationId == organizationId).Select(entity => entity.UserId);
        return await dbContext.Users.AsNoTracking().Where(entity => userIds.Contains(entity.Id)).ToArrayAsync(cancellationToken);
    }

    private static byte[] Encrypt(ReadOnlySpan<byte> plaintext, string passphrase, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);
        var artifact = new byte[HeaderLength + plaintext.Length];
        Magic.CopyTo(artifact, 0);
        BinaryPrimitives.WriteInt32BigEndian(artifact.AsSpan(8, sizeof(int)), SchemaVersion);
        BinaryPrimitives.WriteInt32BigEndian(artifact.AsSpan(12, sizeof(int)), iterations);
        salt.CopyTo(artifact, 16);
        nonce.CopyTo(artifact, 16 + SaltLength);
        var tag = artifact.AsSpan(16 + SaltLength + NonceLength, TagLength);
        var ciphertext = artifact.AsSpan(HeaderLength);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, artifact.AsSpan(0, 16 + SaltLength + NonceLength));
            return artifact;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> artifact, string passphrase)
    {
        if (!artifact[..Magic.Length].SequenceEqual(Magic)) throw PlatformProblemException.Validation(["Backup artifact signature is invalid."]);
        var version = BinaryPrimitives.ReadInt32BigEndian(artifact.Slice(8, sizeof(int)));
        var iterations = BinaryPrimitives.ReadInt32BigEndian(artifact.Slice(12, sizeof(int)));
        if (version != SchemaVersion) throw PlatformProblemException.Validation([$"Backup schema version {version} is not supported."]);
        if (iterations is < 100000 or > 5000000) throw PlatformProblemException.Validation(["Backup key-derivation parameters are invalid."]);
        var salt = artifact.Slice(16, SaltLength);
        var nonce = artifact.Slice(16 + SaltLength, NonceLength);
        var tag = artifact.Slice(16 + SaltLength + NonceLength, TagLength);
        var ciphertext = artifact[HeaderLength..];
        var plaintext = new byte[ciphertext.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, artifact[..(16 + SaltLength + NonceLength)]);
            return plaintext;
        }
        catch (AuthenticationTagMismatchException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw PlatformProblemException.Validation(["Backup authentication failed. The passphrase is wrong or the artifact was modified."]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void ValidatePassphrase(string passphrase)
    {
        if (passphrase.Length is < 12 or > 1024 || passphrase.Any(char.IsControl))
            throw PlatformProblemException.Validation(["Backup passphrase must contain 12-1,024 characters without control characters."]);
    }

    private static void ValidatePayload(BackupPayload payload)
    {
        if (payload.SchemaVersion != SchemaVersion) throw PlatformProblemException.Validation(["Backup payload schema is not supported."]);
        var organizationId = payload.Organization.Id;
        var workspaceIds = payload.Workspaces.Select(entity => entity.Id).ToHashSet();
        var userIds = payload.Users.Select(entity => entity.Id).ToHashSet();
        var errors = new List<string>(payload.Organization.Validate());
        errors.AddRange(payload.Workspaces.SelectMany(entity => entity.Validate()));
        errors.AddRange(payload.Users.SelectMany(entity => entity.Validate()));
        errors.AddRange(payload.Memberships.SelectMany(entity => entity.Validate()));
        errors.AddRange(payload.GroupRoleMappings.SelectMany(entity => entity.Validate()));
        errors.AddRange(payload.Folders.SelectMany(entity => entity.Validate()));
        errors.AddRange(payload.Connections.SelectMany(entity => entity.Validate()));
        errors.AddRange(payload.DynamicGroups.SelectMany(entity => entity.Validate()));
        errors.AddRange(payload.ConnectionTemplates.SelectMany(entity => entity.Validate()));
        errors.AddRange(payload.ConfigurationDrafts.SelectMany(entity => entity.Validate()));
        errors.AddRange(payload.ServiceAccounts.SelectMany(entity => entity.Validate()));
        if (payload.Workspaces.Any(entity => entity.OrganizationId != organizationId) ||
            payload.Memberships.Any(entity => entity.OrganizationId != organizationId || !userIds.Contains(entity.UserId) || entity.WorkspaceId is not null && !workspaceIds.Contains(entity.WorkspaceId.Value)) ||
            payload.GroupRoleMappings.Any(entity => entity.OrganizationId != organizationId || entity.WorkspaceId is not null && !workspaceIds.Contains(entity.WorkspaceId.Value)) ||
            payload.Folders.Any(entity => entity.OrganizationId != organizationId || !workspaceIds.Contains(entity.WorkspaceId)) ||
            payload.Connections.Any(entity => entity.OrganizationId != organizationId || !workspaceIds.Contains(entity.WorkspaceId)) ||
            payload.DynamicGroups.Any(entity => entity.OrganizationId != organizationId || !workspaceIds.Contains(entity.WorkspaceId)) ||
            payload.ConnectionTemplates.Any(entity => entity.OrganizationId != organizationId || !workspaceIds.Contains(entity.WorkspaceId)) ||
            payload.UserWorkspaceStates.Any(entity => entity.OrganizationId != organizationId || !workspaceIds.Contains(entity.WorkspaceId) || !userIds.Contains(entity.UserId)) ||
            payload.ConfigurationDrafts.Any(entity => entity.OrganizationId != organizationId) ||
            payload.PublishedConfigurations.Any(entity => entity.OrganizationId != organizationId) ||
            payload.ServiceAccounts.Any(entity => entity.OrganizationId != organizationId) ||
            payload.SyncChanges.Any(entity => entity.OrganizationId != organizationId || !workspaceIds.Contains(entity.WorkspaceId) || entity.AudienceUserId is not null && !userIds.Contains(entity.AudienceUserId.Value)) ||
            payload.AuditEvents.Any(entity => entity.OrganizationId != organizationId))
            errors.Add("Backup payload contains a resource outside its organization or workspace boundary.");
        if (payload.Connections.Any(entity => entity.OwnerUserId is not null && !userIds.Contains(entity.OwnerUserId.Value)))
            errors.Add("Backup contains a personal connection with an unknown owner.");
        if (errors.Count > 0) throw PlatformProblemException.Validation(errors.Distinct(StringComparer.Ordinal).ToArray());
    }

    private sealed record BackupPayload(
        int SchemaVersion,
        DateTimeOffset CreatedAtUtc,
        Organization Organization,
        Workspace[] Workspaces,
        PlatformUser[] Users,
        WorkspaceMembership[] Memberships,
        GroupRoleMapping[] GroupRoleMappings,
        ManagedFolder[] Folders,
        ManagedConnection[] Connections,
        DynamicConnectionGroup[] DynamicGroups,
        ManagedConnectionTemplate[] ConnectionTemplates,
        UserWorkspaceState[] UserWorkspaceStates,
        ConfigurationDraft[] ConfigurationDrafts,
        PublishedConfiguration[] PublishedConfigurations,
        ServiceAccount[] ServiceAccounts,
        SyncChange[] SyncChanges,
        PlatformAuditEvent[] AuditEvents);
}
