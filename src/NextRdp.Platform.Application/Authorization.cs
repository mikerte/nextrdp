using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Application;

public enum PlatformAction
{
    ReadOrganization,
    ManageOrganization,
    ReadWorkspace,
    ManageWorkspace,
    ReadInventory,
    Connect,
    ManagePersonalInventory,
    ManageSharedInventory,
    ReadConfiguration,
    ManageConfiguration,
    PublishConfiguration,
    ReadAudit,
    ManageMemberships,
    ManageServiceAccounts,
    RunBulkAutomation,
    RunGatewayDiagnostics,
    ManageProductivity,
    CreateBackup,
    RestoreBackup
}

public sealed record PlatformActor(
    Guid PrincipalId,
    string PrincipalKey,
    Guid? OrganizationId,
    IReadOnlySet<WorkspaceRole> Roles,
    IReadOnlyList<string> Groups,
    bool MfaSatisfied,
    long SessionVersion,
    bool IsServiceAccount = false,
    IReadOnlySet<string>? Scopes = null)
{
    public static PlatformActor Anonymous { get; } = new(
        Guid.Empty,
        string.Empty,
        null,
        new HashSet<WorkspaceRole>(),
        [],
        false,
        0,
        false,
        new HashSet<string>());

    public bool IsAuthenticated => PrincipalId != Guid.Empty && !string.IsNullOrWhiteSpace(PrincipalKey);
}

public sealed record AuthorizationTarget(
    Guid OrganizationId,
    Guid? WorkspaceId,
    string ResourceType,
    Guid ResourceId);

public sealed record AuthorizationDecision(bool Allowed, string Reason, IReadOnlyList<string> AppliedRules)
{
    public static AuthorizationDecision Deny(string reason) => new(false, reason, []);
    public static AuthorizationDecision Allow(string reason, params string[] rules) => new(true, reason, rules);
}

public interface IResourceAuthorizationService
{
    Task<AuthorizationDecision> AuthorizeAsync(
        PlatformActor actor,
        PlatformAction action,
        AuthorizationTarget target,
        CancellationToken cancellationToken = default);
}

public interface IAttributePolicyService
{
    Task<AuthorizationDecision> EvaluateAsync(
        PlatformActor actor,
        PlatformAction action,
        AuthorizationTarget target,
        IReadOnlyDictionary<string, object?> attributes,
        CancellationToken cancellationToken = default);
}

public interface IResourceRelationshipService
{
    Task EnsureRoleAsync(
        Guid principalId,
        bool isServiceAccount,
        WorkspaceRole role,
        AuthorizationTarget target,
        CancellationToken cancellationToken = default);

    Task RemoveRoleAsync(
        Guid principalId,
        bool isServiceAccount,
        WorkspaceRole role,
        AuthorizationTarget target,
        CancellationToken cancellationToken = default);
}

public sealed class FixedRoleAuthorizer
{
    private static readonly Dictionary<WorkspaceRole, IReadOnlySet<PlatformAction>> Permissions =
        new Dictionary<WorkspaceRole, IReadOnlySet<PlatformAction>>
        {
            [WorkspaceRole.PlatformAdministrator] = Enum.GetValues<PlatformAction>().ToHashSet(),
            [WorkspaceRole.OrganizationAdministrator] = Enum.GetValues<PlatformAction>().ToHashSet(),
            [WorkspaceRole.WorkspaceAdministrator] = new HashSet<PlatformAction>
            {
                PlatformAction.ReadOrganization,
                PlatformAction.ReadWorkspace,
                PlatformAction.ManageWorkspace,
                PlatformAction.ReadInventory,
                PlatformAction.Connect,
                PlatformAction.ManagePersonalInventory,
                PlatformAction.ManageSharedInventory,
                PlatformAction.ReadConfiguration,
                PlatformAction.ManageConfiguration,
                PlatformAction.PublishConfiguration,
                PlatformAction.ReadAudit,
                PlatformAction.RunBulkAutomation,
                PlatformAction.RunGatewayDiagnostics,
                PlatformAction.ManageProductivity
            },
            [WorkspaceRole.Operator] = new HashSet<PlatformAction>
            {
                PlatformAction.ReadOrganization,
                PlatformAction.ReadWorkspace,
                PlatformAction.ReadInventory,
                PlatformAction.Connect,
                PlatformAction.ManagePersonalInventory,
                PlatformAction.ReadConfiguration,
                PlatformAction.RunGatewayDiagnostics,
                PlatformAction.ManageProductivity
            },
            [WorkspaceRole.Auditor] = new HashSet<PlatformAction>
            {
                PlatformAction.ReadOrganization,
                PlatformAction.ReadWorkspace,
                PlatformAction.ReadInventory,
                PlatformAction.ReadConfiguration,
                PlatformAction.ReadAudit
            }
        };

    public static AuthorizationDecision Authorize(PlatformActor actor, PlatformAction action, Guid organizationId)
    {
        if (!actor.IsAuthenticated) return AuthorizationDecision.Deny("Authentication is required.");
        if (organizationId == Guid.Empty) return AuthorizationDecision.Deny("An organization scope is required.");
        if (actor.OrganizationId is not null && actor.OrganizationId != organizationId && !actor.Roles.Contains(WorkspaceRole.PlatformAdministrator))
            return AuthorizationDecision.Deny("The principal is outside the requested organization scope.");
        if (actor.IsServiceAccount && !ServiceScopeAllows(actor.Scopes ?? new HashSet<string>(), action))
            return AuthorizationDecision.Deny("The service account scope does not allow this action.");

        var matchingRoles = actor.Roles.Where(role => Permissions.TryGetValue(role, out var allowed) && allowed.Contains(action)).ToArray();
        return matchingRoles.Length == 0
            ? AuthorizationDecision.Deny("The assigned Phase 2 role does not allow this action.")
            : AuthorizationDecision.Allow("Allowed by a fixed Phase 2 role.", matchingRoles.Select(role => role.ToString()).ToArray());
    }

    private static bool ServiceScopeAllows(IReadOnlySet<string> scopes, PlatformAction action) => action switch
    {
        PlatformAction.ReadOrganization or PlatformAction.ReadWorkspace or PlatformAction.ReadInventory or PlatformAction.Connect => scopes.Contains("inventory.read"),
        PlatformAction.ManagePersonalInventory or PlatformAction.ManageSharedInventory => scopes.Contains("inventory.write"),
        PlatformAction.ReadConfiguration => scopes.Contains("configuration.read"),
        PlatformAction.ManageConfiguration or PlatformAction.PublishConfiguration => scopes.Contains("configuration.write"),
        PlatformAction.ReadAudit => scopes.Contains("audit.read"),
        PlatformAction.RunBulkAutomation => scopes.Contains("bulk.write"),
        PlatformAction.RunGatewayDiagnostics => scopes.Contains("inventory.read"),
        PlatformAction.ManageProductivity => scopes.Contains("inventory.write"),
        _ => false
    };
}
