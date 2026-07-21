using System.Text.Json;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Application.Tests;

public sealed class PolicyTests
{
    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1", -1)]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    [InlineData("2.1.0", "2.0.99", 1)]
    public void SemanticVersionUsesSemverPrecedence(string left, string right, int expectedSign)
    {
        Assert.True(SemanticVersion.TryParse(left, out var leftVersion));
        Assert.True(SemanticVersion.TryParse(right, out var rightVersion));
        Assert.Equal(expectedSign, Math.Sign(leftVersion.CompareTo(rightVersion)));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-alpha.01")]
    [InlineData("latest")]
    public void SemanticVersionRejectsAmbiguousValues(string value) =>
        Assert.False(SemanticVersion.TryParse(value, out _));

    [Fact]
    public void PolicySchemaRejectsUnknownFields()
    {
        using var document = JsonDocument.Parse("{\"credentials.password\":\"no\"}");

        Assert.Contains("Unsupported policy field 'credentials.password'.", PolicySchema.Validate(document.RootElement, []));
    }

    [Fact]
    public void LockRequiresValueAtSameLayer()
    {
        using var document = JsonDocument.Parse("{}");

        Assert.Contains("Locked policy field 'redirection.clipboard' must have a value at the same layer.",
            PolicySchema.Validate(document.RootElement, ["redirection.clipboard"]));
    }

    [Fact]
    public void LockedOrganizationFieldRejectsConnectionOverride()
    {
        var organizationId = Guid.CreateVersion7();
        var workspaceId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        using var organization = JsonDocument.Parse("{\"redirection.clipboard\":\"Disabled\"}");
        using var connection = JsonDocument.Parse("{\"redirection.clipboard\":\"Bidirectional\"}");
        var layers = new[]
        {
            new ConfigurationLayerInput(ConfigurationScopeType.Organization, organizationId, 1, organization.RootElement.Clone(), new HashSet<string> { "redirection.clipboard" }),
            new ConfigurationLayerInput(ConfigurationScopeType.Connection, connectionId, 1, connection.RootElement.Clone(), new HashSet<string>())
        };

        var effective = ConfigurationResolver.Resolve(organizationId, workspaceId, null, connectionId, layers);

        Assert.Single(effective.Conflicts);
        Assert.Equal("\"Disabled\"", effective.Fields["redirection.clipboard"].JsonValue);
        Assert.True(effective.Fields["redirection.clipboard"].Locked);
    }

    [Fact]
    public void UnlockedLayersUseApprovedPrecedence()
    {
        var organizationId = Guid.CreateVersion7();
        var workspaceId = Guid.CreateVersion7();
        using var organization = JsonDocument.Parse("{\"session.reconnectAttempts\":3}");
        using var workspace = JsonDocument.Parse("{\"session.reconnectAttempts\":8}");

        var effective = ConfigurationResolver.Resolve(organizationId, workspaceId, null, null,
        [
            new(ConfigurationScopeType.Workspace, workspaceId, 2, workspace.RootElement.Clone(), new HashSet<string>()),
            new(ConfigurationScopeType.Organization, organizationId, 1, organization.RootElement.Clone(), new HashSet<string>())
        ]);

        Assert.Empty(effective.Conflicts);
        Assert.Equal("8", effective.Fields["session.reconnectAttempts"].JsonValue);
        Assert.Equal(ConfigurationScopeType.Workspace, effective.Fields["session.reconnectAttempts"].Source);
    }

    [Fact]
    public void PublicSyncAndExportContractsContainNoForbiddenMember()
    {
        var findings = ContractSafety.FindForbiddenPublicMembers(
            typeof(SyncRequest),
            typeof(SyncResponse),
            typeof(ManagedConnectionDto),
            typeof(ExportEnvelope),
            typeof(BulkProvisionRequest));

        Assert.Empty(findings);
    }
}
