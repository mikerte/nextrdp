using Microsoft.EntityFrameworkCore;
using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Infrastructure;

public sealed partial class PlatformManagementService
{
    public async Task<IReadOnlyList<PlatformUserDto>> ListUsersAsync(PlatformActor actor, Guid organizationId, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ManageMemberships, organizationId, null, "organization", organizationId, cancellationToken: cancellationToken);
        return await dbContext.Memberships.AsNoTracking()
            .Where(membership => membership.OrganizationId == organizationId)
            .Select(membership => membership.UserId)
            .Distinct()
            .Join(dbContext.Users.AsNoTracking(), userId => userId, user => user.Id, (_, user) => user)
            .OrderBy(user => user.DisplayName)
            .ThenBy(user => user.Id)
            .Select(user => new PlatformUserDto(
                user.Id, user.DisplayName, user.EmailHint, user.Status, user.LastAuthenticatedAtUtc, user.SessionVersion))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MembershipDto>> ListMembershipsAsync(PlatformActor actor, Guid organizationId, Guid? workspaceId, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ManageMemberships, organizationId, workspaceId, workspaceId is null ? "organization" : "workspace", workspaceId ?? organizationId, cancellationToken: cancellationToken);
        var query = dbContext.Memberships.AsNoTracking().Where(entity => entity.OrganizationId == organizationId);
        if (workspaceId is not null) query = query.Where(entity => entity.WorkspaceId == workspaceId);
        return await query.OrderBy(entity => entity.WorkspaceId).ThenBy(entity => entity.UserId).ThenBy(entity => entity.Role)
            .Select(entity => new MembershipDto(entity.Id, entity.OrganizationId, entity.WorkspaceId, entity.UserId, entity.Role, entity.Status, entity.SourceGroup, entity.Revision, entity.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<MembershipDto> SaveMembershipAsync(PlatformActor actor, Guid organizationId, Guid? membershipId, UpsertMembershipRequest request, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ManageMemberships, organizationId, request.WorkspaceId, request.WorkspaceId is null ? "organization" : "workspace", request.WorkspaceId ?? organizationId, cancellationToken: cancellationToken);
        await EnsureOrganizationExistsAsync(organizationId, cancellationToken);
        if (request.WorkspaceId is not null) await EnsureWorkspaceExistsAsync(organizationId, request.WorkspaceId.Value, cancellationToken);
        if (!await dbContext.Users.AnyAsync(entity => entity.Id == request.UserId, cancellationToken)) throw PlatformProblemException.NotFound("user");
        if (request.Role == WorkspaceRole.PlatformAdministrator) throw PlatformProblemException.Validation(["Platform-administrator assignment is controlled by the identity provider, not workspace membership."]);

        var now = timeProvider.GetUtcNow();
        WorkspaceMembership entity;
        WorkspaceRole? previousRole = null;
        Guid? previousWorkspaceId = null;
        var previouslyActive = false;
        if (membershipId is null)
        {
            if (request.ExpectedRevision is not null) throw PlatformProblemException.Validation(["A new membership cannot include an expected revision."]);
            entity = new WorkspaceMembership { OrganizationId = organizationId, UserId = request.UserId, CreatedAtUtc = now };
            dbContext.Memberships.Add(entity);
        }
        else
        {
            entity = await dbContext.Memberships.SingleOrDefaultAsync(candidate => candidate.OrganizationId == organizationId && candidate.Id == membershipId, cancellationToken)
                ?? throw PlatformProblemException.NotFound("membership");
            RequireRevision(request.ExpectedRevision, entity.Revision);
            previousRole = entity.Role;
            previousWorkspaceId = entity.WorkspaceId;
            previouslyActive = entity.Status == MembershipStatus.Active;
            entity.Revision++;
        }

        entity.WorkspaceId = request.WorkspaceId;
        entity.Role = request.Role;
        entity.Status = request.Status;
        entity.SourceGroup = request.SourceGroup.Trim();
        entity.UpdatedAtUtc = now;
        Validate(entity.Validate());
        var duplicate = await dbContext.Memberships.AnyAsync(candidate => candidate.OrganizationId == organizationId && candidate.UserId == request.UserId &&
            candidate.WorkspaceId == request.WorkspaceId && candidate.Role == request.Role && candidate.Id != entity.Id, cancellationToken);
        if (duplicate) throw PlatformProblemException.Conflict("That user already has this role at the requested scope.");

        if (relationships is not null)
        {
            if (previouslyActive && previousRole is not null)
                await ApplyMembershipRelationshipsAsync(entity.UserId, previousRole.Value, organizationId, previousWorkspaceId, false, cancellationToken);
            if (entity.Status == MembershipStatus.Active)
                await ApplyMembershipRelationshipsAsync(entity.UserId, entity.Role, organizationId, entity.WorkspaceId, true, cancellationToken);
        }

        RecordAudit(actor, organizationId, entity.WorkspaceId, membershipId is null ? "membership.created" : "membership.updated", "membership", entity.Id,
            AuditOutcome.Succeeded, new { entity.UserId, entity.WorkspaceId, role = entity.Role.ToString(), status = entity.Status.ToString(), entity.Revision });
        RecordOutbox(membershipId is null ? "membership.created" : "membership.updated", new { entity.OrganizationId, entity.WorkspaceId, entity.Id, entity.UserId, entity.Revision });
        if (entity.WorkspaceId is not null)
            RecordSync(organizationId, entity.WorkspaceId.Value, entity.UserId, "membership", entity.Id,
                entity.Status == MembershipStatus.Active ? SyncChangeKind.Upsert : SyncChangeKind.AccessRevoked, entity.Revision,
                entity.Status == MembershipStatus.Active ? new { entity.Id, entity.WorkspaceId, entity.Role, entity.Status, entity.Revision } : null);
        await SaveChangesAsync(cancellationToken);
        return Membership(entity);
    }

    public async Task RevokeMembershipAsync(PlatformActor actor, Guid organizationId, Guid membershipId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.Memberships.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.OrganizationId == organizationId && candidate.Id == membershipId, cancellationToken)
            ?? throw PlatformProblemException.NotFound("membership");
        _ = await SaveMembershipAsync(actor, organizationId, membershipId,
            new UpsertMembershipRequest(entity.UserId, entity.WorkspaceId, entity.Role, MembershipStatus.Revoked, entity.SourceGroup, expectedRevision), cancellationToken);
    }

    public async Task<IReadOnlyList<GroupRoleMappingDto>> ListGroupRoleMappingsAsync(PlatformActor actor, Guid organizationId, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ManageMemberships, organizationId, null, "organization", organizationId, cancellationToken: cancellationToken);
        return await dbContext.GroupRoleMappings.AsNoTracking().Where(entity => entity.OrganizationId == organizationId)
            .OrderBy(entity => entity.GroupClaim).ThenBy(entity => entity.WorkspaceId).ThenBy(entity => entity.Role)
            .Select(entity => new GroupRoleMappingDto(entity.Id, entity.OrganizationId, entity.WorkspaceId, entity.GroupClaim, entity.Role, entity.Enabled, entity.Revision, entity.UpdatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<GroupRoleMappingDto> SaveGroupRoleMappingAsync(PlatformActor actor, Guid organizationId, Guid? mappingId, SaveGroupRoleMappingRequest request, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ManageMemberships, organizationId, request.WorkspaceId, request.WorkspaceId is null ? "organization" : "workspace", request.WorkspaceId ?? organizationId, cancellationToken: cancellationToken);
        if (!actor.MfaSatisfied) throw PlatformProblemException.Forbidden("The identity provider must confirm MFA before changing group-to-role mappings.");
        if (request.WorkspaceId is not null) await EnsureWorkspaceExistsAsync(organizationId, request.WorkspaceId.Value, cancellationToken);
        if (request.Role == WorkspaceRole.PlatformAdministrator) throw PlatformProblemException.Validation(["Platform-administrator assignment cannot be created through a group mapping."]);
        var now = timeProvider.GetUtcNow();
        GroupRoleMapping entity;
        var created = mappingId is null;
        if (created)
        {
            entity = new GroupRoleMapping { OrganizationId = organizationId, CreatedAtUtc = now };
            dbContext.GroupRoleMappings.Add(entity);
        }
        else
        {
            entity = await dbContext.GroupRoleMappings.SingleOrDefaultAsync(candidate => candidate.OrganizationId == organizationId && candidate.Id == mappingId, cancellationToken)
                ?? throw PlatformProblemException.NotFound("group-role mapping");
            RequireRevision(request.ExpectedRevision, entity.Revision);
            entity.Revision++;
        }
        entity.WorkspaceId = request.WorkspaceId;
        entity.GroupClaim = request.GroupClaim.Trim();
        entity.Role = request.Role;
        entity.Enabled = request.Enabled;
        entity.UpdatedAtUtc = now;
        Validate(entity.Validate());
        var duplicate = await dbContext.GroupRoleMappings.AnyAsync(candidate => candidate.OrganizationId == organizationId && candidate.WorkspaceId == entity.WorkspaceId &&
            candidate.GroupClaim == entity.GroupClaim && candidate.Role == entity.Role && candidate.Id != entity.Id, cancellationToken);
        if (duplicate) throw PlatformProblemException.Conflict("That group already maps to the selected role and scope.");
        RecordAudit(actor, organizationId, entity.WorkspaceId, created ? "group_role_mapping.created" : "group_role_mapping.updated", "groupRoleMapping", entity.Id,
            AuditOutcome.Succeeded, new { entity.WorkspaceId, entity.GroupClaim, role = entity.Role.ToString(), entity.Enabled, entity.Revision });
        await SaveChangesAsync(cancellationToken);
        return new GroupRoleMappingDto(entity.Id, entity.OrganizationId, entity.WorkspaceId, entity.GroupClaim, entity.Role, entity.Enabled, entity.Revision, entity.UpdatedAtUtc);
    }

    public async Task DeleteGroupRoleMappingAsync(PlatformActor actor, Guid organizationId, Guid mappingId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ManageMemberships, organizationId, null, "groupRoleMapping", mappingId, cancellationToken: cancellationToken);
        if (!actor.MfaSatisfied) throw PlatformProblemException.Forbidden("The identity provider must confirm MFA before deleting a group-to-role mapping.");
        var mapping = await dbContext.GroupRoleMappings.SingleOrDefaultAsync(entity => entity.OrganizationId == organizationId && entity.Id == mappingId, cancellationToken)
            ?? throw PlatformProblemException.NotFound("group-role mapping");
        RequireRevision(expectedRevision, mapping.Revision);
        var memberships = await dbContext.Memberships.Where(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == mapping.WorkspaceId &&
            entity.Role == mapping.Role && entity.SourceGroup == mapping.GroupClaim && entity.Status == MembershipStatus.Active).ToListAsync(cancellationToken);
        foreach (var membership in memberships)
        {
            membership.Status = MembershipStatus.Revoked;
            membership.Revision++;
            membership.UpdatedAtUtc = timeProvider.GetUtcNow();
            if (relationships is not null)
                await ApplyMembershipRelationshipsAsync(membership.UserId, membership.Role, organizationId, membership.WorkspaceId, false, cancellationToken);
            if (membership.WorkspaceId is not null)
                RecordSync(organizationId, membership.WorkspaceId.Value, membership.UserId, "membership", membership.Id, SyncChangeKind.AccessRevoked, membership.Revision, null);
        }
        dbContext.GroupRoleMappings.Remove(mapping);
        RecordAudit(actor, organizationId, mapping.WorkspaceId, "group_role_mapping.deleted", "groupRoleMapping", mapping.Id, AuditOutcome.Succeeded,
            new { revokedMemberships = memberships.Count, revision = mapping.Revision + 1 });
        await SaveChangesAsync(cancellationToken);
    }

    private static AuthorizationTarget MembershipTarget(Guid organizationId, Guid? workspaceId) => workspaceId is null
        ? new AuthorizationTarget(organizationId, null, "organization", organizationId)
        : new AuthorizationTarget(organizationId, workspaceId, "workspace", workspaceId.Value);

    private async Task ApplyMembershipRelationshipsAsync(
        Guid userId,
        WorkspaceRole role,
        Guid organizationId,
        Guid? workspaceId,
        bool ensure,
        CancellationToken cancellationToken)
    {
        if (relationships is null) return;
        var targets = new List<AuthorizationTarget> { MembershipTarget(organizationId, workspaceId) };
        if (workspaceId is null)
        {
            var workspaceIds = await dbContext.Workspaces.AsNoTracking()
                .Where(entity => entity.OrganizationId == organizationId)
                .Select(entity => entity.Id)
                .ToListAsync(cancellationToken);
            targets.AddRange(workspaceIds.Select(id => new AuthorizationTarget(organizationId, id, "workspace", id)));
        }

        foreach (var target in targets)
        {
            if (ensure) await relationships.EnsureRoleAsync(userId, false, role, target, cancellationToken);
            else await relationships.RemoveRoleAsync(userId, false, role, target, cancellationToken);
        }
    }

    private static MembershipDto Membership(WorkspaceMembership entity) => new(
        entity.Id, entity.OrganizationId, entity.WorkspaceId, entity.UserId, entity.Role, entity.Status, entity.SourceGroup, entity.Revision, entity.UpdatedAtUtc);
}
