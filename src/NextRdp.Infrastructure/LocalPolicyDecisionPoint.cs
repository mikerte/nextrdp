using NextRdp.Core;

namespace NextRdp.Infrastructure;

public sealed class LocalPolicyDecisionPoint : IPolicyDecisionPoint
{
    public Task<PolicyDecision> AuthorizeLaunchAsync(ConnectionProfile connection, CancellationToken cancellationToken = default)
    {
        var errors = connection.Validate();
        return Task.FromResult(errors.Count == 0
            ? new PolicyDecision(true, "Phase 1 local validation passed.", ["phase1-local-policy"])
            : new PolicyDecision(false, string.Join(" ", errors), ["phase1-local-policy"]));
    }
}
