using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using NextRdp.Core;
using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Infrastructure;

public sealed partial class PlatformManagementService
{
    private static readonly HashSet<string> AllowedServiceAccountScopes = new(StringComparer.Ordinal)
    {
        "inventory.read",
        "inventory.write",
        "configuration.read",
        "configuration.write",
        "audit.read",
        "bulk.write"
    };

    public async Task<BulkProvisionResult> BulkProvisionAsync(
        PlatformActor actor,
        Guid organizationId,
        Guid workspaceId,
        string idempotencyKey,
        BulkProvisionRequest request,
        CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.RunBulkAutomation, organizationId, workspaceId, "workspace", workspaceId, cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200 || idempotencyKey.Any(char.IsControl))
            throw PlatformProblemException.Validation(["Idempotency key is required, cannot exceed 200 characters, and cannot contain control characters."]);
        if (request.Items.Count is < 1 or > 500) throw PlatformProblemException.Validation(["Bulk provisioning requires between 1 and 500 items."]);
        var duplicateKeys = request.Items.GroupBy(item => item.ExternalKey, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicateKeys.Length > 0) throw PlatformProblemException.Validation(["Bulk provisioning external keys must be unique."]);

        var actorKey = actor.PrincipalKey.Length <= 300 ? actor.PrincipalKey : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actor.PrincipalKey)));
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestJson)));
        var existingIdempotency = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(entity =>
            entity.OrganizationId == organizationId && entity.ActorKey == actorKey && entity.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existingIdempotency is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(existingIdempotency.RequestHash), Convert.FromHexString(requestHash)))
                throw PlatformProblemException.Conflict("The idempotency key was already used with a different request.");
            var replay = JsonSerializer.Deserialize<BulkProvisionResult>(existingIdempotency.ResponseJson, JsonOptions)
                ?? throw new InvalidOperationException("Stored idempotency response is invalid.");
            return replay with { Replayed = true };
        }

        await using var transaction = request.Atomic ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        var results = new List<BulkItemResult>(request.Items.Count);
        foreach (var item in request.Items)
        {
            try
            {
                ValidateExternalKey(item.ExternalKey);
                var existingId = await dbContext.Connections.AsNoTracking()
                    .Where(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && entity.ProvisioningKey == item.ExternalKey)
                    .Select(entity => (Guid?)entity.Id)
                    .SingleOrDefaultAsync(cancellationToken);
                var requestWithRevision = item.Connection;
                if (existingId is not null)
                {
                    var revision = await dbContext.Connections.AsNoTracking().Where(entity => entity.Id == existingId).Select(entity => entity.Revision).SingleAsync(cancellationToken);
                    requestWithRevision = item.Connection with { ExpectedRevision = revision };
                }

                var saved = await SaveConnectionAsync(actor, organizationId, workspaceId, existingId, requestWithRevision, cancellationToken);
                var tracked = await dbContext.Connections.SingleAsync(entity => entity.Id == saved.Id, cancellationToken);
                tracked.ProvisioningKey = item.ExternalKey;
                await SaveChangesAsync(cancellationToken);
                results.Add(new BulkItemResult(item.ExternalKey, saved.Id, true, "ok", existingId is null ? "Created." : "Updated."));
            }
            catch (PlatformProblemException exception)
            {
                if (request.Atomic) throw;
                dbContext.ChangeTracker.Clear();
                results.Add(new BulkItemResult(item.ExternalKey, null, false, exception.Code, exception.Message));
            }
        }

        var result = new BulkProvisionResult(results, false);
        var responseJson = JsonSerializer.Serialize(result, JsonOptions);
        dbContext.IdempotencyRecords.Add(new IdempotencyRecord
        {
            OrganizationId = organizationId,
            ActorKey = actorKey,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            StatusCode = results.All(item => item.Succeeded) ? 200 : 207,
            ResponseJson = responseJson,
            CreatedAtUtc = timeProvider.GetUtcNow(),
            ExpiresAtUtc = timeProvider.GetUtcNow().AddHours(24)
        });
        RecordAudit(actor, organizationId, workspaceId, "bulk.provisioned", "workspace", workspaceId, AuditOutcome.Succeeded,
            new { request.Items.Count, succeeded = results.Count(item => item.Succeeded), failed = results.Count(item => !item.Succeeded), request.Atomic });
        await SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<ManagedImportPreview> PreviewImportAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, ImportPreviewRequest request, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ManageSharedInventory, organizationId, workspaceId, "workspace", workspaceId, cancellationToken: cancellationToken);
        if (Encoding.UTF8.GetByteCount(request.Content) > 2 * 1024 * 1024) throw PlatformProblemException.Validation(["Import content cannot exceed 2 MiB."]);
        var candidates = request.Format.Trim().ToLowerInvariant() switch
        {
            "csv" => ParseCsvImport(request.Content),
            "json" => ParseJsonImport(request.Content),
            _ => throw PlatformProblemException.Validation(["Import format must be 'csv' or 'json'."])
        };
        if (candidates.Count is < 1 or > 500) throw PlatformProblemException.Validation(["Import must contain between 1 and 500 rows."]);
        candidates = await MarkImportDuplicatesAsync(organizationId, workspaceId, candidates, cancellationToken);
        var secretFieldsDiscarded = CountDiscardedSecretFields(request.Content, request.Format);
        var now = timeProvider.GetUtcNow();
        var record = new ManagedImportPreviewRecord
        {
            OrganizationId = organizationId,
            WorkspaceId = workspaceId,
            ActorId = actor.PrincipalId,
            CandidatesJson = JsonSerializer.Serialize(candidates, JsonOptions),
            SecretFieldsDiscarded = secretFieldsDiscarded,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(30)
        };
        dbContext.ImportPreviews.Add(record);
        RecordAudit(actor, organizationId, workspaceId, "import.previewed", "importPreview", record.Id, AuditOutcome.Succeeded,
            new { format = request.Format.Trim().ToLowerInvariant(), candidates = candidates.Count, invalid = candidates.Count(item => item.Errors.Count > 0), secretFieldsDiscarded });
        await SaveChangesAsync(cancellationToken);
        return new ManagedImportPreview(record.Id, candidates, secretFieldsDiscarded, record.ExpiresAtUtc);
    }

    public async Task<BulkProvisionResult> CommitImportAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, CommitImportRequest request, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ManageSharedInventory, organizationId, workspaceId, "workspace", workspaceId, cancellationToken: cancellationToken);
        var preview = await dbContext.ImportPreviews.SingleOrDefaultAsync(entity =>
            entity.Id == request.PreviewId && entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && entity.ActorId == actor.PrincipalId, cancellationToken)
            ?? throw PlatformProblemException.NotFound("import preview");
        if (preview.ExpiresAtUtc <= timeProvider.GetUtcNow()) throw new PlatformProblemException(410, "import_preview_expired", "Import preview expired", "Create a new import preview before committing.");
        var candidates = JsonSerializer.Deserialize<ImportCandidateDto[]>(preview.CandidatesJson, JsonOptions) ?? [];
        var selected = request.SelectedExternalKeys.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (selected.Count is < 1 or > 500) throw PlatformProblemException.Validation(["Select between 1 and 500 import rows."]);
        var selectedCandidates = candidates.Where(candidate => selected.Contains(candidate.ExternalKey)).ToArray();
        if (selectedCandidates.Length != selected.Count) throw PlatformProblemException.Validation(["One or more selected import rows do not exist in this preview."]);
        if (selectedCandidates.Any(candidate => candidate.Connection is null || candidate.Errors.Count > 0))
            throw PlatformProblemException.Validation(["Invalid import rows cannot be committed."]);

        var result = await BulkProvisionAsync(actor, organizationId, workspaceId, $"import:{preview.Id:N}",
            new BulkProvisionRequest(true, selectedCandidates.Select(candidate => new BulkConnectionItem(candidate.ExternalKey, candidate.Connection!)).ToArray()), cancellationToken);
        preview.CommittedAtUtc ??= timeProvider.GetUtcNow();
        RecordAudit(actor, organizationId, workspaceId, "import.committed", "importPreview", preview.Id, AuditOutcome.Succeeded,
            new { selected = selected.Count, preview.SecretFieldsDiscarded });
        await SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<ExportEnvelope> ExportInventoryAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ReadInventory, organizationId, workspaceId, "workspace", workspaceId, cancellationToken: cancellationToken);
        var folders = await dbContext.Folders.AsNoTracking().Where(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId)
            .OrderBy(entity => entity.SortOrder).ThenBy(entity => entity.Name).ThenBy(entity => entity.Id).ToListAsync(cancellationToken);
        var connections = await dbContext.Connections.AsNoTracking().Where(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId &&
            (entity.Visibility == ResourceVisibility.Shared || entity.OwnerUserId == actor.PrincipalId))
            .OrderBy(entity => entity.Name).ThenBy(entity => entity.Id).ToListAsync(cancellationToken);
        var envelope = new ExportEnvelope(1, timeProvider.GetUtcNow(), organizationId, workspaceId, folders.Select(entity => entity.ToDto()).ToArray(), connections.Select(entity => entity.ToDto()).ToArray());
        var forbidden = ContractSafety.FindForbiddenPublicMembers(typeof(ExportEnvelope));
        if (forbidden.Count > 0) throw new InvalidOperationException("Export contract contains a forbidden member.");
        RecordAudit(actor, organizationId, workspaceId, "inventory.exported", "workspace", workspaceId, AuditOutcome.Succeeded,
            new { folders = folders.Count, connections = connections.Count, schemaVersion = envelope.SchemaVersion });
        await SaveChangesAsync(cancellationToken);
        return envelope;
    }

    public async Task<IReadOnlyList<ServiceAccountDto>> ListServiceAccountsAsync(PlatformActor actor, Guid organizationId, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ManageServiceAccounts, organizationId, null, "organization", organizationId, cancellationToken: cancellationToken);
        var accounts = await dbContext.ServiceAccounts.AsNoTracking().Where(entity => entity.OrganizationId == organizationId).OrderBy(entity => entity.Name).ToListAsync(cancellationToken);
        return accounts.Select(ToServiceAccountDto).ToArray();
    }

    public async Task<CreatedServiceAccount> CreateServiceAccountAsync(PlatformActor actor, Guid organizationId, CreateServiceAccountRequest request, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ManageServiceAccounts, organizationId, request.WorkspaceId, "organization", organizationId, cancellationToken: cancellationToken);
        if (!actor.MfaSatisfied) throw PlatformProblemException.Forbidden("The identity provider must confirm MFA before creating a service account.");
        var scopes = request.Scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (scopes.Length is < 1 or > 16 || scopes.Any(scope => !AllowedServiceAccountScopes.Contains(scope)))
            throw PlatformProblemException.Validation(["Service account scopes contain an unsupported or empty value."]);
        if (request.ExpiresAtUtc is not null && (request.ExpiresAtUtc <= timeProvider.GetUtcNow() || request.ExpiresAtUtc > timeProvider.GetUtcNow().AddDays(365)))
            throw PlatformProblemException.Validation(["Service account expiry must be within the next 365 days."]);
        if (request.WorkspaceId is not null) await EnsureWorkspaceExistsAsync(organizationId, request.WorkspaceId.Value, cancellationToken);

        var clientId = $"nextrdp_{Guid.CreateVersion7():N}";
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var clientSecret = Convert.ToBase64String(secretBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var account = new ServiceAccount
        {
            OrganizationId = organizationId,
            WorkspaceId = request.WorkspaceId,
            Name = request.Name.Trim(),
            ClientId = clientId,
            ScopesJson = JsonSerializer.Serialize(scopes, JsonOptions),
            ExpiresAtUtc = request.ExpiresAtUtc,
            CreatedByUserId = actor.PrincipalId,
            CreatedAtUtc = timeProvider.GetUtcNow()
        };
        Validate(account.Validate());

        await serviceAccountCredentials.StoreAsync(clientId, clientSecret, scopes, request.ExpiresAtUtc, cancellationToken);
        var relationshipsAttempted = false;
        try
        {
            if (relationships is not null)
            {
                relationshipsAttempted = true;
                await EnsureServiceAccountRelationshipsAsync(account.Id, organizationId, request.WorkspaceId, cancellationToken);
            }
            dbContext.ServiceAccounts.Add(account);
            RecordAudit(actor, organizationId, request.WorkspaceId, "service_account.created", "serviceAccount", account.Id, AuditOutcome.Succeeded,
                new { account.Name, account.ClientId, scopes, account.ExpiresAtUtc });
            await SaveChangesAsync(cancellationToken);
        }
        catch
        {
            if (relationshipsAttempted && relationships is not null)
            {
                try { await RemoveServiceAccountRelationshipsAsync(account.Id, organizationId, request.WorkspaceId, cancellationToken); }
                catch { /* Preserve the creation failure; the account was never persisted or issued. */ }
            }
            await serviceAccountCredentials.RevokeAsync(clientId, cancellationToken);
            CryptographicOperations.ZeroMemory(secretBytes);
            throw;
        }
        CryptographicOperations.ZeroMemory(secretBytes);
        return new CreatedServiceAccount(account.Id, account.Name, account.ClientId, clientSecret, account.ExpiresAtUtc);
    }

    public async Task RevokeServiceAccountAsync(PlatformActor actor, Guid organizationId, Guid serviceAccountId, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ManageServiceAccounts, organizationId, null, "serviceAccount", serviceAccountId, cancellationToken: cancellationToken);
        if (!actor.MfaSatisfied) throw PlatformProblemException.Forbidden("The identity provider must confirm MFA before revoking a service account.");
        var account = await dbContext.ServiceAccounts.SingleOrDefaultAsync(entity => entity.OrganizationId == organizationId && entity.Id == serviceAccountId, cancellationToken)
            ?? throw PlatformProblemException.NotFound("service account");
        if (account.RevokedAtUtc is null)
        {
            account.RevokedAtUtc = timeProvider.GetUtcNow();
            RecordAudit(actor, organizationId, account.WorkspaceId, "service_account.revoked", "serviceAccount", account.Id, AuditOutcome.Succeeded, new { account.ClientId });
            await SaveChangesAsync(cancellationToken);
        }
        await serviceAccountCredentials.RevokeAsync(account.ClientId, cancellationToken);
        if (relationships is not null)
            await RemoveServiceAccountRelationshipsAsync(account.Id, organizationId, account.WorkspaceId, cancellationToken);
    }

    public async Task<RotatedServiceAccountSecret> RotateServiceAccountSecretAsync(
        PlatformActor actor,
        Guid organizationId,
        Guid serviceAccountId,
        CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ManageServiceAccounts, organizationId, null, "serviceAccount", serviceAccountId, cancellationToken: cancellationToken);
        if (!actor.MfaSatisfied) throw PlatformProblemException.Forbidden("The identity provider must confirm MFA before rotating a service-account secret.");
        var account = await dbContext.ServiceAccounts.AsNoTracking().SingleOrDefaultAsync(entity =>
            entity.OrganizationId == organizationId && entity.Id == serviceAccountId, cancellationToken)
            ?? throw PlatformProblemException.NotFound("service account");
        if (account.RevokedAtUtc is not null) throw PlatformProblemException.Conflict("A revoked service account cannot be rotated.");
        if (account.ExpiresAtUtc is not null && account.ExpiresAtUtc <= timeProvider.GetUtcNow())
            throw PlatformProblemException.Conflict("An expired service account cannot be rotated.");

        var secretBytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            var clientSecret = Convert.ToBase64String(secretBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var scopes = JsonSerializer.Deserialize<string[]>(account.ScopesJson, JsonOptions) ?? [];
            await serviceAccountCredentials.RotateAsync(account.ClientId, clientSecret, scopes, account.ExpiresAtUtc, cancellationToken);
            RecordAudit(actor, organizationId, account.WorkspaceId, "service_account.secret_rotated", "serviceAccount", account.Id,
                AuditOutcome.Succeeded, new { account.ClientId });
            await SaveChangesAsync(cancellationToken);
            return new RotatedServiceAccountSecret(account.Id, account.ClientId, clientSecret, timeProvider.GetUtcNow());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    private static AuthorizationTarget ServiceAccountTarget(Guid organizationId, Guid? workspaceId) => workspaceId is null
        ? new AuthorizationTarget(organizationId, null, "organization", organizationId)
        : new AuthorizationTarget(organizationId, workspaceId, "workspace", workspaceId.Value);

    private async Task EnsureServiceAccountRelationshipsAsync(Guid accountId, Guid organizationId, Guid? workspaceId, CancellationToken cancellationToken)
    {
        if (relationships is null) return;
        if (workspaceId is not null)
        {
            // Workspace-scoped automation still needs read-only organization discovery so clients can
            // resolve the one organization and workspace they are already constrained to.
            await relationships.EnsureRoleAsync(accountId, true, WorkspaceRole.Operator,
                new AuthorizationTarget(organizationId, null, "organization", organizationId), cancellationToken);
        }
        await relationships.EnsureRoleAsync(accountId, true, WorkspaceRole.OrganizationAdministrator,
            ServiceAccountTarget(organizationId, workspaceId), cancellationToken);
        if (workspaceId is null)
        {
            var workspaceIds = await dbContext.Workspaces.AsNoTracking()
                .Where(entity => entity.OrganizationId == organizationId)
                .Select(entity => entity.Id)
                .ToListAsync(cancellationToken);
            foreach (var id in workspaceIds)
                await relationships.EnsureRoleAsync(accountId, true, WorkspaceRole.OrganizationAdministrator,
                    new AuthorizationTarget(organizationId, id, "workspace", id), cancellationToken);
        }
    }

    private async Task RemoveServiceAccountRelationshipsAsync(Guid accountId, Guid organizationId, Guid? workspaceId, CancellationToken cancellationToken)
    {
        if (relationships is null) return;
        await relationships.RemoveRoleAsync(accountId, true, WorkspaceRole.OrganizationAdministrator,
            ServiceAccountTarget(organizationId, workspaceId), cancellationToken);
        if (workspaceId is null)
        {
            var workspaceIds = await dbContext.Workspaces.AsNoTracking()
                .Where(entity => entity.OrganizationId == organizationId)
                .Select(entity => entity.Id)
                .ToListAsync(cancellationToken);
            foreach (var id in workspaceIds)
                await relationships.RemoveRoleAsync(accountId, true, WorkspaceRole.OrganizationAdministrator,
                    new AuthorizationTarget(organizationId, id, "workspace", id), cancellationToken);
        }
        if (workspaceId is not null)
        {
            await relationships.RemoveRoleAsync(accountId, true, WorkspaceRole.Operator,
                new AuthorizationTarget(organizationId, null, "organization", organizationId), cancellationToken);
        }
    }

    private static List<ImportCandidateDto> ParseCsvImport(string content)
    {
        using var reader = new StringReader(content);
        using var parser = new TextFieldParser(reader)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = true
        };
        parser.SetDelimiters(",");
        var headers = parser.ReadFields() ?? throw PlatformProblemException.Validation(["CSV header is required."]);
        if (headers.Length is < 1 or > 100 || headers.Any(string.IsNullOrWhiteSpace)) throw PlatformProblemException.Validation(["CSV headers must be non-empty and contain at most 100 columns."]);
        var normalizedHeaders = headers.Select(header => header.Trim()).ToArray();
        if (normalizedHeaders.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalizedHeaders.Length) throw PlatformProblemException.Validation(["CSV headers must be unique."]);

        var result = new List<ImportCandidateDto>();
        while (!parser.EndOfData)
        {
            if (result.Count >= 500) throw PlatformProblemException.Validation(["CSV import cannot exceed 500 rows."]);
            var values = parser.ReadFields() ?? [];
            if (values.Length != normalizedHeaders.Length)
            {
                result.Add(new ImportCandidateDto($"row-{result.Count + 1}", null, ["CSV row has a different number of fields than the header."], []));
                continue;
            }
            var fields = normalizedHeaders.Zip(values).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.OrdinalIgnoreCase);
            var key = fields.GetValueOrDefault("externalKey") ?? $"row-{result.Count + 1}";
            result.Add(BuildImportCandidate(key, fields));
        }
        return result;
    }

    private async Task<List<ImportCandidateDto>> MarkImportDuplicatesAsync(
        Guid organizationId,
        Guid workspaceId,
        List<ImportCandidateDto> candidates,
        CancellationToken cancellationToken)
    {
        var existingTargets = (await dbContext.Connections.AsNoTracking()
                .Where(connection => connection.OrganizationId == organizationId && connection.WorkspaceId == workspaceId)
                .Select(connection => new { connection.Host, connection.Port })
                .ToListAsync(cancellationToken))
            .Select(connection => ConnectionIdentity.CreateKey(connection.Host, connection.Port))
            .ToHashSet(StringComparer.Ordinal);
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);
        var seenExternalKeys = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ImportCandidateDto>(candidates.Count);

        foreach (var candidate in candidates)
        {
            var errors = candidate.Errors.ToList();
            if (!seenExternalKeys.Add(candidate.ExternalKey)) errors.Add("External key duplicates an earlier import row.");
            if (candidate.Connection is { } connection)
            {
                var identity = ConnectionIdentity.CreateKey(connection.Host, connection.Port);
                if (existingTargets.Contains(identity)) errors.Add("Target and port already exist in this workspace.");
                else if (!seenTargets.Add(identity)) errors.Add("Target and port duplicate an earlier import row.");
            }
            result.Add(candidate with
            {
                Connection = errors.Count == 0 ? candidate.Connection : null,
                Errors = errors.Distinct(StringComparer.Ordinal).ToArray()
            });
        }
        return result;
    }

    private static List<ImportCandidateDto> ParseJsonImport(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw PlatformProblemException.Validation(["JSON import root must be an array."]);
            var result = new List<ImportCandidateDto>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (result.Count >= 500) throw PlatformProblemException.Validation(["JSON import cannot exceed 500 rows."]);
                if (item.ValueKind != JsonValueKind.Object)
                {
                    result.Add(new ImportCandidateDto($"row-{result.Count + 1}", null, ["JSON import row must be an object."], []));
                    continue;
                }
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in item.EnumerateObject())
                {
                    if (IsSecretField(property.Name)) continue;
                    if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                        fields[property.Name] = property.Value.ToString();
                    else if (property.Name.Equals("tags", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Array)
                        fields[property.Name] = string.Join(';', property.Value.EnumerateArray().Where(tag => tag.ValueKind == JsonValueKind.String).Select(tag => tag.GetString()));
                }
                var key = fields.GetValueOrDefault("externalKey") ?? $"row-{result.Count + 1}";
                result.Add(BuildImportCandidate(key, fields));
            }
            return result;
        }
        catch (JsonException exception)
        {
            throw PlatformProblemException.Validation([$"JSON import is malformed at byte position {exception.BytePositionInLine}."]);
        }
    }

    private static ImportCandidateDto BuildImportCandidate(string externalKey, Dictionary<string, string> fields)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (!fields.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name)) errors.Add("Name is required.");
        if (!fields.TryGetValue("host", out var host) || string.IsNullOrWhiteSpace(host)) errors.Add("Host is required.");
        var port = 3389;
        if (fields.TryGetValue("port", out var portText) && !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port)) errors.Add("Port must be an integer.");
        var environment = ConnectionEnvironment.Development;
        if (fields.TryGetValue("environment", out var environmentText) && !Enum.TryParse(environmentText, true, out environment)) errors.Add("Environment is invalid.");
        var visibility = ResourceVisibility.Shared;
        if (fields.TryGetValue("visibility", out var visibilityText) && !Enum.TryParse(visibilityText, true, out visibility)) errors.Add("Visibility is invalid.");
        var tags = fields.GetValueOrDefault("tags")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        var request = new UpsertManagedConnectionRequest(
            null,
            visibility,
            name ?? string.Empty,
            host ?? string.Empty,
            port,
            fields.GetValueOrDefault("usernameHint") ?? string.Empty,
            environment,
            fields.GetValueOrDefault("notes") ?? string.Empty,
            tags,
            new ConnectionMetadataDto(
                fields.GetValueOrDefault("businessUnit") ?? string.Empty,
                fields.GetValueOrDefault("applicationOwner") ?? string.Empty,
                fields.GetValueOrDefault("serviceOwner") ?? string.Empty,
                fields.GetValueOrDefault("criticality") ?? string.Empty,
                fields.GetValueOrDefault("geography") ?? string.Empty,
                fields.GetValueOrDefault("dataClassification") ?? string.Empty,
                fields.GetValueOrDefault("maintenanceWindow") ?? string.Empty,
                fields.GetValueOrDefault("supportGroup") ?? string.Empty,
                fields.GetValueOrDefault("escalationContact") ?? string.Empty,
                fields.GetValueOrDefault("assetId") ?? string.Empty,
                fields.GetValueOrDefault("cmdbId") ?? string.Empty,
                fields.GetValueOrDefault("operatingSystem") ?? string.Empty,
                fields.GetValueOrDefault("monitoringUrl") ?? string.Empty,
                fields.GetValueOrDefault("documentationUrl") ?? string.Empty,
                fields.GetValueOrDefault("ticketingUrl") ?? string.Empty),
            DefaultManagedRdpSettings(),
            null);
        var validationEntity = new ManagedConnection
        {
            OrganizationId = Guid.CreateVersion7(),
            WorkspaceId = Guid.CreateVersion7(),
            OwnerUserId = visibility == ResourceVisibility.Personal ? Guid.CreateVersion7() : null,
            Visibility = visibility,
            Name = request.Name.Trim(),
            Host = request.Host.Trim(),
            Port = request.Port,
            UsernameHint = request.UsernameHint.Trim(),
            Environment = request.Environment,
            Notes = request.Notes,
            Tags = request.Tags.ToList(),
            Metadata = request.Metadata.ToDomain(),
            Settings = request.Settings.ToDomain()
        };
        errors.AddRange(validationEntity.Validate());
        try { ValidateExternalKey(externalKey); }
        catch (PlatformProblemException exception) { errors.Add(exception.Message); }
        return new ImportCandidateDto(externalKey, errors.Count == 0 ? request : null, errors.Distinct(StringComparer.Ordinal).ToArray(), warnings);
    }

    private static int CountDiscardedSecretFields(string content, string format)
    {
        if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            using var firstLineReader = new StringReader(content);
            using var parser = new TextFieldParser(firstLineReader) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
            parser.SetDelimiters(",");
            var headers = parser.ReadFields() ?? [];
            var secretIndexes = headers.Select((header, index) => (header, index)).Where(item => IsSecretField(item.header)).Select(item => item.index).ToHashSet();
            var count = 0;
            while (!parser.EndOfData)
            {
                var values = parser.ReadFields() ?? [];
                count += secretIndexes.Count(index => index < values.Length && !string.IsNullOrWhiteSpace(values[index]));
            }
            return count;
        }

        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
            return CountSecrets(document.RootElement);
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static int CountSecrets(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Sum(property => (IsSecretField(property.Name) && property.Value.ValueKind != JsonValueKind.Null ? 1 : 0) + CountSecrets(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Sum(CountSecrets),
            _ => 0
        };
    }

    private static bool IsSecretField(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("privateKey", StringComparison.OrdinalIgnoreCase);

    private static void ValidateExternalKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl))
            throw PlatformProblemException.Validation(["External key is required, cannot exceed 200 characters, and cannot contain control characters."]);
    }

    private static ManagedRdpSettingsDto DefaultManagedRdpSettings() => new(
        ManagedConnectionKind.Desktop,
        true,
        false,
        string.Empty,
        GatewayUsage.Never,
        string.Empty,
        443,
        string.Empty,
        string.Empty,
        string.Empty,
        SessionDisplayMode.Embedded,
        [],
        null,
        null,
        32,
        PerformancePreset.Auto,
        true,
        ThumbnailMode.Disabled,
        ShortcutRouting.RemoteInFullScreen,
        true,
        false,
        false,
        false,
        false,
        false,
        false,
        false,
        false,
        true,
        TransferDirection.Bidirectional,
        TransferDirection.Disabled,
        0,
        5);

    private static ServiceAccountDto ToServiceAccountDto(ServiceAccount account)
    {
        var scopes = JsonSerializer.Deserialize<string[]>(account.ScopesJson, JsonOptions) ?? [];
        return new ServiceAccountDto(account.Id, account.Name, account.ClientId, account.WorkspaceId, scopes, account.ExpiresAtUtc, account.RevokedAtUtc);
    }
}
