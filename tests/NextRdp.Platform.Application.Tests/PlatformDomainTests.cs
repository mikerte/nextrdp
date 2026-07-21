using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Application.Tests;

public sealed class PlatformDomainTests
{
    [Fact]
    public void PersonalConnectionRequiresOwner()
    {
        var connection = ValidConnection();
        connection.Visibility = ResourceVisibility.Personal;

        Assert.Contains("Personal connections require an owner.", connection.Validate());
    }

    [Fact]
    public void SharedConnectionHasNoCredentialMember()
    {
        var names = typeof(ManagedConnection).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(ValidConnection().Validate());
    }

    [Fact]
    public void MetadataRejectsNonHttpsAndCredentialedLinks()
    {
        var metadata = new ManagedConnectionMetadata
        {
            MonitoringUrl = "http://monitor.example.test/item/1",
            DocumentationUrl = "https://user:secret@docs.example.test/item/1"
        };

        var errors = metadata.Validate();
        Assert.Contains(errors, error => error.StartsWith("Monitoring URL", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("Documentation URL", StringComparison.Ordinal));
    }

    [Fact]
    public void SelectedMonitorModeRequiresUniqueMonitors()
    {
        var settings = new ManagedRdpSettings
        {
            DisplayMode = SessionDisplayMode.SelectedMonitors,
            SelectedMonitorIds = [1, 1]
        };

        var errors = settings.Validate();
        Assert.Contains("Select at most 16 unique monitor identifiers.", errors);
    }

    [Fact]
    public void RemoteAppRequiresProgram()
    {
        var settings = new ManagedRdpSettings { Kind = ManagedConnectionKind.RemoteApp };

        Assert.Contains("RemoteApp program is required for a RemoteApp connection.", settings.Validate());
    }

    [Fact]
    public void ServiceAccountNeverContainsSecretMaterial()
    {
        var names = typeof(ServiceAccount).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(names, name => name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProductSessionExpiryAndRevocationBoundariesAreDeterministic()
    {
        var now = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var session = new ProductSession
        {
            SessionVersion = 4,
            IdleExpiresAtUtc = now.AddMinutes(5),
            AbsoluteExpiresAtUtc = now.AddHours(1)
        };

        Assert.True(session.IsActive(now, 4));
        Assert.False(session.IsActive(now, 5));

        session.IdleExpiresAtUtc = now;
        Assert.False(session.IsActive(now, 4));
        session.IdleExpiresAtUtc = now.AddMinutes(5);
        session.AbsoluteExpiresAtUtc = now;
        Assert.False(session.IsActive(now, 4));
        session.AbsoluteExpiresAtUtc = now.AddHours(1);
        session.RevokedAtUtc = now.AddMinutes(-1);
        Assert.False(session.IsActive(now, 4));
    }

    [Fact]
    public void ProductSessionActivityNeverExtendsPastAbsoluteExpiry()
    {
        var now = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var absolute = now.AddMinutes(10);
        var session = new ProductSession
        {
            SessionVersion = 1,
            IdleTimeoutMinutes = 30,
            IdleExpiresAtUtc = now.AddMinutes(5),
            AbsoluteExpiresAtUtc = absolute,
            LastActivityAtUtc = now.AddMinutes(-1)
        };

        session.RecordActivity(now);

        Assert.Equal(now, session.LastActivityAtUtc);
        Assert.Equal(absolute, session.IdleExpiresAtUtc);
        Assert.False(session.IsActive(absolute, 1));
    }

    private static ManagedConnection ValidConnection() => new()
    {
        OrganizationId = Guid.CreateVersion7(),
        WorkspaceId = Guid.CreateVersion7(),
        Name = "Lab server",
        Host = "lab.example.test"
    };
}
