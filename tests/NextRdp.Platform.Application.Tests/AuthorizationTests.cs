using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Application.Tests;

public sealed class AuthorizationTests
{
    [Theory]
    [InlineData(WorkspaceRole.OrganizationAdministrator, PlatformAction.ManageMemberships, true)]
    [InlineData(WorkspaceRole.WorkspaceAdministrator, PlatformAction.PublishConfiguration, true)]
    [InlineData(WorkspaceRole.Operator, PlatformAction.Connect, true)]
    [InlineData(WorkspaceRole.Operator, PlatformAction.ManageSharedInventory, false)]
    [InlineData(WorkspaceRole.Auditor, PlatformAction.ReadAudit, true)]
    [InlineData(WorkspaceRole.Auditor, PlatformAction.Connect, false)]
    public void FixedRolesHaveExplicitPermissions(WorkspaceRole role, PlatformAction action, bool expected)
    {
        var organizationId = Guid.CreateVersion7();
        var actor = Actor(organizationId, role);

        Assert.Equal(expected, FixedRoleAuthorizer.Authorize(actor, action, organizationId).Allowed);
    }

    [Fact]
    public void CrossOrganizationRequestIsDenied()
    {
        var actor = Actor(Guid.CreateVersion7(), WorkspaceRole.OrganizationAdministrator);

        var decision = FixedRoleAuthorizer.Authorize(actor, PlatformAction.ReadInventory, Guid.CreateVersion7());

        Assert.False(decision.Allowed);
        Assert.Contains("outside", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnonymousRequestIsDenied()
    {
        Assert.False(FixedRoleAuthorizer.Authorize(PlatformActor.Anonymous, PlatformAction.ReadInventory, Guid.CreateVersion7()).Allowed);
    }

    private static PlatformActor Actor(Guid organizationId, WorkspaceRole role) =>
        new(Guid.CreateVersion7(), "issuer|subject", organizationId, new HashSet<WorkspaceRole> { role }, [], true, 1);
}
