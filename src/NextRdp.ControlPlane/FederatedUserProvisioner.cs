using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;
using NextRdp.Platform.Infrastructure;
using OpenIddict.Abstractions;

namespace NextRdp.ControlPlane;

public sealed record ProvisionedIdentity(
    PlatformUser User,
    IReadOnlySet<WorkspaceRole> Roles,
    IReadOnlyList<string> Groups,
    bool MfaSatisfied,
    Guid? OrganizationId);

public interface IFederatedUserProvisioner
{
    Task<ProvisionedIdentity> ProvisionAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}

public sealed class FederatedUserProvisioner(
    PlatformDbContext dbContext,
    IConfiguration configuration,
    TimeProvider timeProvider,
    IResourceRelationshipService relationships,
    CorporateMfaPolicy mfaPolicy) : IFederatedUserProvisioner
{
    public async Task<ProvisionedIdentity> ProvisionAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var development = principal.HasClaim("nextrdp_development_identity", "true");
        var issuer = principal.FindFirst(OpenIddictConstants.Claims.Issuer)?.Value ??
            (development ? "https://development.nextrdp.local" : configuration["Authentication:CorporateOidc:Authority"]);
        var subject = principal.FindFirst(OpenIddictConstants.Claims.Subject)?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
            throw PlatformProblemException.Forbidden("The corporate identity is missing a stable issuer or subject.");
        var mfa = mfaPolicy.Evaluate(principal, development);

        var displayName = principal.FindFirst(OpenIddictConstants.Claims.Name)?.Value ?? principal.Identity?.Name ?? "Corporate user";
        var email = principal.FindFirst(OpenIddictConstants.Claims.Email)?.Value ?? string.Empty;
        var now = timeProvider.GetUtcNow();
        var user = await dbContext.Users.SingleOrDefaultAsync(entity => entity.Issuer == issuer && entity.Subject == subject, cancellationToken);
        if (user is null)
        {
            user = new PlatformUser { Issuer = issuer, Subject = subject };
            dbContext.Users.Add(user);
        }
        user.DisplayName = displayName.Trim();
        user.EmailHint = email.Trim();
        user.LastAuthenticatedAtUtc = now;
        var errors = user.Validate();
        if (errors.Count > 0) throw PlatformProblemException.Validation(errors);
        await dbContext.SaveChangesAsync(cancellationToken);

        var groups = principal.Claims.Where(claim => claim.Type is "groups" or "group").Select(claim => claim.Value).Where(value => value.Length <= 512).Distinct(StringComparer.Ordinal).Take(200).ToArray();
        Guid? organizationId = Guid.TryParse(principal.FindFirst("nextrdp_org_id")?.Value, out var organization) ? organization : null;
        var roles = new HashSet<WorkspaceRole>();
        var mappings = configuration.GetSection("Authentication:CorporateOidc:GroupRoleMappings").GetChildren()
            .ToDictionary(section => section.Key, section => section.Value ?? string.Empty, StringComparer.Ordinal);
        foreach (var group in groups)
            if (mappings.TryGetValue(group, out var mapped) && Enum.TryParse<WorkspaceRole>(mapped, true, out var role)) roles.Add(role);
        var persistedMappings = organizationId is null ? [] : await dbContext.GroupRoleMappings
            .Where(mapping => mapping.OrganizationId == organizationId && mapping.Enabled && groups.Contains(mapping.GroupClaim))
            .ToListAsync(cancellationToken);
        foreach (var mapping in persistedMappings) roles.Add(mapping.Role);
        if (development) roles.Add(WorkspaceRole.PlatformAdministrator);
        foreach (var role in roles)
        {
            if (role == WorkspaceRole.PlatformAdministrator)
                await relationships.EnsureRoleAsync(user.Id, false, role, new AuthorizationTarget(Guid.Empty, null, "platform", Guid.Empty), cancellationToken);
            if (organizationId is not null)
                await ApplyOrganizationRoleAsync(user.Id, organizationId.Value, role, true, cancellationToken);
        }
        if (organizationId is not null)
            await ReconcileGroupMembershipsAsync(user.Id, organizationId.Value, groups, persistedMappings, now, cancellationToken);
        return new ProvisionedIdentity(user, roles, groups, mfa, organizationId);
    }

    private async Task ReconcileGroupMembershipsAsync(Guid userId, Guid organizationId, IReadOnlyList<string> groups, IReadOnlyList<GroupRoleMapping> activeMappings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await dbContext.Memberships.Where(membership => membership.OrganizationId == organizationId && membership.UserId == userId).ToListAsync(cancellationToken);
        var managedGroupClaims = await dbContext.GroupRoleMappings.AsNoTracking()
            .Where(mapping => mapping.OrganizationId == organizationId)
            .Select(mapping => mapping.GroupClaim)
            .Distinct()
            .ToListAsync(cancellationToken);
        foreach (var mapping in activeMappings)
        {
            var matching = existing.FirstOrDefault(membership => membership.WorkspaceId == mapping.WorkspaceId && membership.Role == mapping.Role && membership.SourceGroup == mapping.GroupClaim);
            if (matching is null && !existing.Any(membership => membership.WorkspaceId == mapping.WorkspaceId && membership.Role == mapping.Role && membership.Status == MembershipStatus.Active))
            {
                matching = new WorkspaceMembership
                {
                    OrganizationId = organizationId,
                    WorkspaceId = mapping.WorkspaceId,
                    UserId = userId,
                    Role = mapping.Role,
                    Status = MembershipStatus.Active,
                    SourceGroup = mapping.GroupClaim,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                dbContext.Memberships.Add(matching);
                existing.Add(matching);
            }
            else if (matching is not null && matching.Status != MembershipStatus.Active)
            {
                matching.Status = MembershipStatus.Active;
                matching.Revision++;
                matching.UpdatedAtUtc = now;
            }
            if (mapping.WorkspaceId is null)
                await ApplyOrganizationRoleAsync(userId, organizationId, mapping.Role, true, cancellationToken);
            else
                await relationships.EnsureRoleAsync(userId, false, mapping.Role,
                    new AuthorizationTarget(organizationId, mapping.WorkspaceId, "workspace", mapping.WorkspaceId.Value), cancellationToken);
        }

        var activeKeys = activeMappings.Select(mapping => (mapping.WorkspaceId, mapping.Role, mapping.GroupClaim)).ToHashSet();
        foreach (var membership in existing.Where(membership => membership.Status == MembershipStatus.Active && managedGroupClaims.Contains(membership.SourceGroup) &&
                     (!groups.Contains(membership.SourceGroup, StringComparer.Ordinal) || !activeKeys.Contains((membership.WorkspaceId, membership.Role, membership.SourceGroup)))))
        {
            membership.Status = MembershipStatus.Revoked;
            membership.Revision++;
            membership.UpdatedAtUtc = now;
            var anotherGrant = existing.Any(other => other.Id != membership.Id && other.WorkspaceId == membership.WorkspaceId && other.Role == membership.Role && other.Status == MembershipStatus.Active);
            if (!anotherGrant)
            {
                if (membership.WorkspaceId is null)
                    await ApplyOrganizationRoleAsync(userId, organizationId, membership.Role, false, cancellationToken);
                else
                    await relationships.RemoveRoleAsync(userId, false, membership.Role,
                        new AuthorizationTarget(organizationId, membership.WorkspaceId, "workspace", membership.WorkspaceId.Value), cancellationToken);
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ApplyOrganizationRoleAsync(Guid userId, Guid organizationId, WorkspaceRole role, bool ensure, CancellationToken cancellationToken)
    {
        var targets = new List<AuthorizationTarget>
        {
            new(organizationId, null, "organization", organizationId)
        };
        var workspaceIds = await dbContext.Workspaces.AsNoTracking()
            .Where(entity => entity.OrganizationId == organizationId)
            .Select(entity => entity.Id)
            .ToListAsync(cancellationToken);
        targets.AddRange(workspaceIds.Select(id => new AuthorizationTarget(organizationId, id, "workspace", id)));
        foreach (var target in targets)
        {
            if (ensure) await relationships.EnsureRoleAsync(userId, false, role, target, cancellationToken);
            else await relationships.RemoveRoleAsync(userId, false, role, target, cancellationToken);
        }
    }
}
