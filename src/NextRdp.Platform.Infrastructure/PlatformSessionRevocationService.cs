using Microsoft.EntityFrameworkCore;
using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Infrastructure;

public sealed class PlatformSessionRevocationService(
    PlatformDbContext dbContext,
    IResourceAuthorizationService resourceAuthorization,
    IAttributePolicyService attributePolicy,
    TimeProvider timeProvider) : IPlatformSessionRevocationService
{
    public async Task RevokeSelfAsync(PlatformActor actor, CancellationToken cancellationToken = default)
    {
        if (actor.IsServiceAccount) throw PlatformProblemException.Forbidden("Service accounts do not have interactive product sessions.");
        var user = await dbContext.Users.SingleOrDefaultAsync(entity => entity.Id == actor.PrincipalId, cancellationToken)
            ?? throw PlatformProblemException.NotFound("user");
        var now = timeProvider.GetUtcNow();
        user.SessionVersion++;
        var sessions = await dbContext.ProductSessions
            .Where(entity => entity.UserId == actor.PrincipalId && entity.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions) session.RevokedAtUtc = now;
        if (actor.OrganizationId is Guid organizationId)
        {
            dbContext.AuditEvents.Add(new PlatformAuditEvent
            {
                OrganizationId = organizationId,
                ActorUserId = actor.PrincipalId,
                Action = "session.global_logout",
                ResourceType = "user",
                ResourceId = actor.PrincipalId,
                Outcome = AuditOutcome.Succeeded,
                RedactedDetailJson = "{\"sessionsRevoked\":true}",
                OccurredAtUtc = now
            });
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokePrincipalAsync(PlatformActor actor, Guid organizationId, Guid principalId, CancellationToken cancellationToken = default)
    {
        var fixedDecision = FixedRoleAuthorizer.Authorize(actor, PlatformAction.ManageMemberships, organizationId);
        if (!fixedDecision.Allowed) throw PlatformProblemException.Forbidden(fixedDecision.Reason);
        var target = new AuthorizationTarget(organizationId, null, "user", principalId);
        var resourceDecision = await resourceAuthorization.AuthorizeAsync(actor, PlatformAction.ManageMemberships, target, cancellationToken);
        if (!resourceDecision.Allowed) throw PlatformProblemException.Forbidden(resourceDecision.Reason);
        var policyDecision = await attributePolicy.EvaluateAsync(actor, PlatformAction.ManageMemberships, target, new Dictionary<string, object?>(), cancellationToken);
        if (!policyDecision.Allowed) throw PlatformProblemException.Forbidden(policyDecision.Reason);

        var user = await dbContext.Users.SingleOrDefaultAsync(entity => entity.Id == principalId, cancellationToken)
            ?? throw PlatformProblemException.NotFound("user");
        user.SessionVersion++;
        var now = timeProvider.GetUtcNow();
        var productSessions = await dbContext.ProductSessions
            .Where(entity => entity.UserId == principalId && entity.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var session in productSessions) session.RevokedAtUtc = now;
        var memberships = await dbContext.Memberships.Where(entity => entity.OrganizationId == organizationId && entity.UserId == principalId && entity.Status == MembershipStatus.Active).ToListAsync(cancellationToken);
        foreach (var membership in memberships) membership.Status = MembershipStatus.Revoked;
        var workspaces = memberships.Where(entity => entity.WorkspaceId is not null).Select(entity => entity.WorkspaceId!.Value).Distinct().ToArray();
        foreach (var workspaceId in workspaces)
        {
            dbContext.SyncChanges.Add(new SyncChange
            {
                OrganizationId = organizationId,
                WorkspaceId = workspaceId,
                AudienceUserId = principalId,
                ResourceType = "access",
                ResourceId = principalId,
                Kind = SyncChangeKind.AccessRevoked,
                ResourceRevision = user.SessionVersion,
                OccurredAtUtc = now
            });
        }
        dbContext.AuditEvents.Add(new PlatformAuditEvent
        {
            OrganizationId = organizationId,
            ActorUserId = actor.PrincipalId,
            Action = "principal.access_revoked",
            ResourceType = "user",
            ResourceId = principalId,
            Outcome = AuditOutcome.Succeeded,
            RedactedDetailJson = "{\"sessionsRevoked\":true}",
            OccurredAtUtc = now
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> IsCurrentAsync(Guid principalId, long sessionVersion, CancellationToken cancellationToken = default) =>
        dbContext.Users.AsNoTracking().AnyAsync(entity => entity.Id == principalId && entity.Status == MembershipStatus.Active && entity.SessionVersion == sessionVersion, cancellationToken);
}
