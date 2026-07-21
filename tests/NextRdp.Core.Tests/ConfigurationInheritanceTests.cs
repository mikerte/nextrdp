using NextRdp.Core;

namespace NextRdp.Core.Tests;

public sealed class ConfigurationInheritanceTests
{
    [Fact]
    public void MostSpecificExplicitOverrideWins()
    {
        var result = ConfigurationInheritance.Resolve(32,
        [
            new(ConfigurationScope.Connection, "connection-1", true, 24),
            new(ConfigurationScope.Organization, "organization-1", true, 16),
            new(ConfigurationScope.Folder, "folder-1", true, 15),
            new(ConfigurationScope.Workspace, "workspace-1", true, 32)
        ]);

        Assert.Equal(24, result.Value);
        Assert.Equal(ConfigurationScope.Connection, result.SourceScope);
        Assert.False(result.IsLocked);
    }

    [Fact]
    public void HigherScopeLockBlocksLowerOverrides()
    {
        var result = ConfigurationInheritance.Resolve(true,
        [
            new(ConfigurationScope.Organization, "organization-1", true, false, IsLocked: true),
            new(ConfigurationScope.Workspace, "workspace-1", true, true),
            new(ConfigurationScope.Connection, "connection-1", true, true)
        ]);

        Assert.False(result.Value);
        Assert.True(result.IsLocked);
        Assert.Equal(ConfigurationScope.Organization, result.LockedByScope);
    }

    [Fact]
    public void LockWithoutOverrideLocksTheInheritedValue()
    {
        var result = ConfigurationInheritance.Resolve(5,
        [
            new(ConfigurationScope.Workspace, "workspace-1", true, 8),
            new(ConfigurationScope.Folder, "folder-1", false, 0, IsLocked: true),
            new(ConfigurationScope.Connection, "connection-1", true, 12)
        ]);

        Assert.Equal(8, result.Value);
        Assert.Equal(ConfigurationScope.Workspace, result.SourceScope);
        Assert.Equal(ConfigurationScope.Folder, result.LockedByScope);
    }

    [Fact]
    public void AmbiguousDuplicateScopesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ConfigurationInheritance.Resolve(0,
        [
            new(ConfigurationScope.Folder, "folder-1", true, 1),
            new(ConfigurationScope.Folder, "folder-2", true, 2)
        ]));
    }
}
