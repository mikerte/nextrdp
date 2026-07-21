using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;
using NextRdp.Platform.Infrastructure;
using OpenIddict.Abstractions;

namespace NextRdp.ControlPlane;

public interface IPlatformActorAccessor
{
    Task<PlatformActor> GetAsync(HttpContext context, CancellationToken cancellationToken = default);
}

public sealed class PlatformActorAccessor(
    PlatformDbContext dbContext,
    IFederatedUserProvisioner provisioner) : IPlatformActorAccessor
{
    public async Task<PlatformActor> GetAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated != true) throw PlatformProblemException.Forbidden("Authentication is required.");
        var isServiceAccount = principal.HasClaim("nextrdp_service_account", "true");
        if (isServiceAccount) return await ServiceAccountAsync(principal, cancellationToken);

        ProvisionedIdentity provisioned;
        var platformIdClaim = principal.FindFirst("nextrdp_user_id")?.Value;
        if (Guid.TryParse(platformIdClaim, out var platformId))
        {
            var user = await dbContext.Users.SingleOrDefaultAsync(entity => entity.Id == platformId, cancellationToken)
                ?? throw PlatformProblemException.Forbidden("The authenticated platform user no longer exists.");
            var tokenVersionClaim = principal.FindFirst("nextrdp_session_version")?.Value;
            if (long.TryParse(tokenVersionClaim, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var tokenVersion) && tokenVersion != user.SessionVersion)
                throw PlatformProblemException.Forbidden("This product session was revoked. Sign in again to continue.");
            var sessionClaim = principal.FindFirst("nextrdp_session_id")?.Value;
            if (Guid.TryParse(sessionClaim, out var sessionId))
            {
                var session = await dbContext.ProductSessions.SingleOrDefaultAsync(entity => entity.Id == sessionId && entity.UserId == platformId, cancellationToken)
                    ?? throw PlatformProblemException.Forbidden("This product session no longer exists. Sign in again to continue.");
                var now = DateTimeOffset.UtcNow;
                if (!session.IsActive(now, user.SessionVersion))
                    throw PlatformProblemException.Forbidden("This product session expired or was revoked. Sign in again to continue.");
                if (now - session.LastActivityAtUtc >= TimeSpan.FromMinutes(1))
                {
                    session.RecordActivity(now);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
            }
            provisioned = new ProvisionedIdentity(user, Roles(principal), Groups(principal), Mfa(principal), OrganizationId(principal));
        }
        else
        {
            provisioned = await provisioner.ProvisionAsync(principal, cancellationToken);
        }

        if (provisioned.User.Status != MembershipStatus.Active) throw PlatformProblemException.Forbidden("The platform user is suspended or revoked.");
        var memberships = await dbContext.Memberships.AsNoTracking()
            .Where(entity => entity.UserId == provisioned.User.Id && entity.Status == MembershipStatus.Active)
            .ToListAsync(cancellationToken);
        var roles = provisioned.Roles.Concat(memberships.Select(entity => entity.Role)).ToHashSet();
        var organizationId = provisioned.OrganizationId;
        if (organizationId is null)
        {
            var organizations = memberships.Select(entity => entity.OrganizationId).Distinct().ToArray();
            if (organizations.Length == 1) organizationId = organizations[0];
        }
        var principalKey = $"{provisioned.User.Issuer}|{provisioned.User.Subject}";
        return new PlatformActor(provisioned.User.Id, principalKey, organizationId, roles, provisioned.Groups, provisioned.MfaSatisfied, provisioned.User.SessionVersion);
    }

    private async Task<PlatformActor> ServiceAccountAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(principal.FindFirst(OpenIddictConstants.Claims.Subject)?.Value, out var serviceAccountId))
            throw PlatformProblemException.Forbidden("Service account subject is invalid.");
        var account = await dbContext.ServiceAccounts.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == serviceAccountId, cancellationToken)
            ?? throw PlatformProblemException.Forbidden("Service account no longer exists.");
        if (account.RevokedAtUtc is not null || account.ExpiresAtUtc is not null && account.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw PlatformProblemException.Forbidden("Service account is revoked or expired.");
        var scopes = principal.Claims.Where(claim => claim.Type == OpenIddictConstants.Claims.Scope)
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);
        return new PlatformActor(account.Id, account.ClientId, account.OrganizationId,
            new HashSet<WorkspaceRole> { WorkspaceRole.OrganizationAdministrator }, [], true, 1, true, scopes);
    }

    private static HashSet<WorkspaceRole> Roles(ClaimsPrincipal principal) =>
        principal.Claims.Where(claim => claim.Type is OpenIddictConstants.Claims.Role or ClaimTypes.Role or "nextrdp_role")
            .Select(claim => Enum.TryParse<WorkspaceRole>(claim.Value, true, out var role) ? (WorkspaceRole?)role : null)
            .OfType<WorkspaceRole>()
            .ToHashSet();

    private static string[] Groups(ClaimsPrincipal principal) =>
        principal.Claims.Where(claim => claim.Type is "groups" or "group").Select(claim => claim.Value).Where(value => value.Length <= 512).Distinct(StringComparer.Ordinal).Take(200).ToArray();

    private static bool Mfa(ClaimsPrincipal principal) =>
        principal.Claims.Any(claim => claim.Type == OpenIddictConstants.Claims.AuthenticationMethodReference && claim.Value.Equals("mfa", StringComparison.OrdinalIgnoreCase)) ||
        principal.HasClaim("nextrdp_development_identity", "true");

    private static Guid? OrganizationId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirst("nextrdp_org_id")?.Value, out var organizationId) ? organizationId : null;
}
