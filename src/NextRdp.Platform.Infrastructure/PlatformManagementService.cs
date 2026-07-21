using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Infrastructure;

public sealed partial class PlatformManagementService : IPlatformManagementService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] OrganizationCreatedFields = ["name", "slug"];
    private static readonly string[] WorkspaceCreatedFields = ["name", "slug", "description"];
    private static readonly string[] FolderMutationFields = ["parentId", "name", "sortOrder"];
    private static readonly string[] ConnectionMutationFields = ["folderId", "visibility", "name", "host", "port", "usernameHint", "environment", "notes", "tags", "metadata", "settings"];
    private readonly PlatformDbContext dbContext;
    private readonly IResourceAuthorizationService resourceAuthorization;
    private readonly IAttributePolicyService attributePolicy;
    private readonly ISyncCursorCodec cursorCodec;
    private readonly IServiceAccountCredentialStore serviceAccountCredentials;
    private readonly TimeProvider timeProvider;
    private readonly IResourceRelationshipService? relationships;

    public PlatformManagementService(
        PlatformDbContext dbContext,
        IResourceAuthorizationService resourceAuthorization,
        IAttributePolicyService attributePolicy,
        ISyncCursorCodec cursorCodec,
        IServiceAccountCredentialStore serviceAccountCredentials,
        TimeProvider timeProvider,
        IResourceRelationshipService? relationships = null)
    {
        this.dbContext = dbContext;
        this.resourceAuthorization = resourceAuthorization;
        this.attributePolicy = attributePolicy;
        this.cursorCodec = cursorCodec;
        this.serviceAccountCredentials = serviceAccountCredentials;
        this.timeProvider = timeProvider;
        this.relationships = relationships;
    }

    public async Task<IReadOnlyList<OrganizationDto>> ListOrganizationsAsync(PlatformActor actor, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(actor);
        IQueryable<Organization> query = dbContext.Organizations.AsNoTracking();
        if (!actor.Roles.Contains(WorkspaceRole.PlatformAdministrator))
        {
            if (actor.OrganizationId is null) return [];
            query = query.Where(entity => entity.Id == actor.OrganizationId);
        }

        var organizations = await query.OrderBy(entity => entity.Name).ThenBy(entity => entity.Id).ToListAsync(cancellationToken);
        var result = new List<OrganizationDto>(organizations.Count);
        foreach (var organization in organizations)
        {
            await DemandAsync(actor, PlatformAction.ReadOrganization, organization.Id, null, "organization", organization.Id, cancellationToken: cancellationToken);
            result.Add(organization.ToDto());
        }
        return result;
    }

    public async Task<OrganizationDto> CreateOrganizationAsync(PlatformActor actor, CreateOrganizationRequest request, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(actor);
        if (!actor.Roles.Contains(WorkspaceRole.PlatformAdministrator)) throw PlatformProblemException.Forbidden("Only a platform administrator can create an organization.");
        if (!actor.MfaSatisfied) throw PlatformProblemException.Forbidden("The identity provider must confirm MFA before organization creation.");
        await DemandExternalAsync(actor, PlatformAction.ManageOrganization, new AuthorizationTarget(Guid.Empty, null, "platform", Guid.Empty), EmptyAttributes, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var organization = new Organization
        {
            Name = request.Name.Trim(),
            Slug = request.Slug.Trim().ToLowerInvariant(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        Validate(organization.Validate());
        if (await dbContext.Organizations.AnyAsync(entity => entity.Slug == organization.Slug, cancellationToken))
            throw PlatformProblemException.Conflict("An organization with this slug already exists.");

        if (relationships is not null)
            await relationships.EnsureRoleAsync(actor.PrincipalId, actor.IsServiceAccount, WorkspaceRole.OrganizationAdministrator,
                new AuthorizationTarget(organization.Id, null, "organization", organization.Id), cancellationToken);

        dbContext.Organizations.Add(organization);
        if (!actor.IsServiceAccount && await dbContext.Users.AnyAsync(entity => entity.Id == actor.PrincipalId, cancellationToken))
        {
            dbContext.Memberships.Add(new WorkspaceMembership
            {
                OrganizationId = organization.Id,
                UserId = actor.PrincipalId,
                Role = WorkspaceRole.OrganizationAdministrator,
                Status = MembershipStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }
        RecordAudit(actor, organization.Id, null, "organization.created", "organization", organization.Id, AuditOutcome.Succeeded, new { fields = OrganizationCreatedFields });
        RecordOutbox("organization.created", new { organization.Id, organization.Revision });
        await SaveChangesAsync(cancellationToken);
        return organization.ToDto();
    }

    public async Task<IReadOnlyList<WorkspaceDto>> ListWorkspacesAsync(PlatformActor actor, Guid organizationId, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ReadWorkspace, organizationId, null, "organization", organizationId, cancellationToken: cancellationToken);
        var workspaces = await dbContext.Workspaces.AsNoTracking()
            .Where(entity => entity.OrganizationId == organizationId)
            .OrderBy(entity => entity.Name)
            .ThenBy(entity => entity.Id)
            .ToListAsync(cancellationToken);
        return workspaces.Select(entity => entity.ToDto()).ToArray();
    }

    public async Task<WorkspaceDto> CreateWorkspaceAsync(PlatformActor actor, Guid organizationId, CreateWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ManageWorkspace, organizationId, null, "organization", organizationId, cancellationToken: cancellationToken);
        await EnsureOrganizationExistsAsync(organizationId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var workspace = new Workspace
        {
            OrganizationId = organizationId,
            Name = request.Name.Trim(),
            Slug = request.Slug.Trim().ToLowerInvariant(),
            Description = request.Description.Trim(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        Validate(workspace.Validate());
        if (await dbContext.Workspaces.AnyAsync(entity => entity.OrganizationId == organizationId && entity.Slug == workspace.Slug, cancellationToken))
            throw PlatformProblemException.Conflict("A workspace with this slug already exists in the organization.");

        if (relationships is not null)
        {
            var workspaceTarget = new AuthorizationTarget(organizationId, workspace.Id, "workspace", workspace.Id);
            await relationships.EnsureRoleAsync(actor.PrincipalId, actor.IsServiceAccount, StrongestWorkspaceRole(actor.Roles),
                workspaceTarget, cancellationToken);

            // Phase 2 materializes organization grants on each workspace. Preserve that inheritance
            // for workspaces created after the organization membership or service account.
            var inheritedMemberships = await dbContext.Memberships.AsNoTracking()
                .Where(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == null && entity.Status == MembershipStatus.Active)
                .Select(entity => new { entity.UserId, entity.Role })
                .ToListAsync(cancellationToken);
            foreach (var membership in inheritedMemberships)
                await relationships.EnsureRoleAsync(membership.UserId, false, membership.Role, workspaceTarget, cancellationToken);

            var inheritedServiceAccounts = await dbContext.ServiceAccounts.AsNoTracking()
                .Where(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == null && entity.RevokedAtUtc == null &&
                    (entity.ExpiresAtUtc == null || entity.ExpiresAtUtc > now))
                .Select(entity => entity.Id)
                .ToListAsync(cancellationToken);
            foreach (var accountId in inheritedServiceAccounts)
                await relationships.EnsureRoleAsync(accountId, true, WorkspaceRole.OrganizationAdministrator, workspaceTarget, cancellationToken);
        }

        dbContext.Workspaces.Add(workspace);
        RecordAudit(actor, organizationId, workspace.Id, "workspace.created", "workspace", workspace.Id, AuditOutcome.Succeeded, new { fields = WorkspaceCreatedFields });
        RecordOutbox("workspace.created", new { workspace.OrganizationId, workspace.Id, workspace.Revision });
        await SaveChangesAsync(cancellationToken);
        return workspace.ToDto();
    }

    private async Task DemandAsync(
        PlatformActor actor,
        PlatformAction action,
        Guid organizationId,
        Guid? workspaceId,
        string resourceType,
        Guid resourceId,
        IReadOnlyDictionary<string, object?>? attributes = null,
        CancellationToken cancellationToken = default)
    {
        var fixedDecision = FixedRoleAuthorizer.Authorize(actor, action, organizationId);
        if (!fixedDecision.Allowed) throw PlatformProblemException.Forbidden(fixedDecision.Reason);
        await DemandExternalAsync(actor, action, new AuthorizationTarget(organizationId, workspaceId, resourceType, resourceId), attributes ?? EmptyAttributes, cancellationToken);
    }

    private async Task DemandExternalAsync(
        PlatformActor actor,
        PlatformAction action,
        AuthorizationTarget target,
        IReadOnlyDictionary<string, object?> attributes,
        CancellationToken cancellationToken)
    {
        var resourceDecision = await resourceAuthorization.AuthorizeAsync(actor, action, target, cancellationToken);
        if (!resourceDecision.Allowed) throw PlatformProblemException.Forbidden(resourceDecision.Reason);
        var policyDecision = await attributePolicy.EvaluateAsync(actor, action, target, attributes, cancellationToken);
        if (!policyDecision.Allowed) throw PlatformProblemException.Forbidden(policyDecision.Reason);
    }

    private async Task EnsureOrganizationExistsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty || !await dbContext.Organizations.AnyAsync(entity => entity.Id == organizationId, cancellationToken))
            throw PlatformProblemException.NotFound("organization");
    }

    private async Task EnsureWorkspaceExistsAsync(Guid organizationId, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (workspaceId == Guid.Empty || !await dbContext.Workspaces.AnyAsync(entity => entity.OrganizationId == organizationId && entity.Id == workspaceId, cancellationToken))
            throw PlatformProblemException.NotFound("workspace");
    }

    private void RecordAudit(PlatformActor actor, Guid organizationId, Guid? workspaceId, string action, string resourceType, Guid? resourceId, AuditOutcome outcome, object detail)
    {
        dbContext.AuditEvents.Add(new PlatformAuditEvent
        {
            OrganizationId = organizationId,
            WorkspaceId = workspaceId,
            ActorUserId = actor.IsServiceAccount ? null : actor.PrincipalId,
            ServiceAccountId = actor.IsServiceAccount ? actor.PrincipalId : null,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Outcome = outcome,
            RedactedDetailJson = JsonSerializer.Serialize(detail, JsonOptions),
            OccurredAtUtc = timeProvider.GetUtcNow()
        });
    }

    private void RecordOutbox(string type, object payload)
    {
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Type = type,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            OccurredAtUtc = timeProvider.GetUtcNow()
        });
    }

    private void RecordSync(Guid organizationId, Guid workspaceId, Guid? audienceUserId, string resourceType, Guid resourceId, SyncChangeKind kind, long revision, object? payload)
    {
        dbContext.SyncChanges.Add(new SyncChange
        {
            OrganizationId = organizationId,
            WorkspaceId = workspaceId,
            AudienceUserId = audienceUserId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Kind = kind,
            ResourceRevision = revision,
            PayloadJson = payload is null ? "{}" : JsonSerializer.Serialize(payload, JsonOptions),
            OccurredAtUtc = timeProvider.GetUtcNow()
        });
    }

    private async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new PlatformProblemException(409, "revision_conflict", "Revision conflict", "The resource changed after it was loaded. Refresh and compare before retrying.",
                new Dictionary<string, string[]> { ["resource"] = exception.Entries.Select(entry => entry.Metadata.ClrType.Name).Distinct().ToArray() });
        }
    }

    private static void EnsureAuthenticated(PlatformActor actor)
    {
        if (!actor.IsAuthenticated) throw PlatformProblemException.Forbidden("Authentication is required.");
    }

    private static void Validate(IReadOnlyList<string> errors)
    {
        if (errors.Count > 0) throw PlatformProblemException.Validation(errors);
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyAttributes = new Dictionary<string, object?>();

    private static WorkspaceRole StrongestWorkspaceRole(IReadOnlySet<WorkspaceRole> roles)
    {
        if (roles.Contains(WorkspaceRole.PlatformAdministrator)) return WorkspaceRole.PlatformAdministrator;
        if (roles.Contains(WorkspaceRole.OrganizationAdministrator)) return WorkspaceRole.OrganizationAdministrator;
        if (roles.Contains(WorkspaceRole.WorkspaceAdministrator)) return WorkspaceRole.WorkspaceAdministrator;
        if (roles.Contains(WorkspaceRole.Auditor)) return WorkspaceRole.Auditor;
        return WorkspaceRole.Operator;
    }
}
