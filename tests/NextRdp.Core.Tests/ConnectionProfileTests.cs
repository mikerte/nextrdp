using NextRdp.Core;

namespace NextRdp.Core.Tests;

public sealed class ConnectionProfileTests
{
    [Fact]
    public void ValidateAcceptsASafeDirectConnection()
    {
        var profile = new ConnectionProfile { Name = "Lab", Host = "lab.example.test", Port = 3389 };
        Assert.Empty(profile.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void ValidateRejectsInvalidPorts(int port)
    {
        var profile = new ConnectionProfile { Name = "Invalid", Host = "host", Port = port };
        Assert.Contains(profile.Validate(), error => error.Contains("Port", StringComparison.Ordinal));
    }

    [Fact]
    public void ModelHasNoPasswordProperty()
    {
        var sensitiveProperties = typeof(ConnectionProfile).GetProperties()
            .Where(x => x.Name.Contains("password", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(sensitiveProperties);
    }

    [Theory]
    [InlineData("bad host")]
    [InlineData("server/path")]
    [InlineData("server\\share")]
    [InlineData("server.example.test:3390")]
    [InlineData("[2001:db8::10]")]
    [InlineData("server?.example.test")]
    [InlineData("server..example.test")]
    [InlineData("-server.example.test")]
    public void ValidateRejectsHostsThatCouldBeMistakenForPaths(string host)
    {
        var profile = new ConnectionProfile { Name = "Invalid", Host = host };
        Assert.Contains(profile.Validate(), error => error.Contains("valid hostname", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateAllowsIpv4Ipv6AndSingleLabelHosts()
    {
        Assert.Empty(new ConnectionProfile { Name = "IPv4", Host = "192.0.2.10" }.Validate());
        Assert.Empty(new ConnectionProfile { Name = "IPv6", Host = "2001:db8::10" }.Validate());
        Assert.Empty(new ConnectionProfile { Name = "NetBIOS", Host = "SERVER01" }.Validate());
    }

    [Theory]
    [InlineData("prod sql")]
    [InlineData("prd-srv")]
    [InlineData("prdsrv01")]
    [InlineData("database")]
    public void SearchMatchesMultipleFieldsAndFuzzyTerms(string query)
    {
        var profile = new ConnectionProfile
        {
            Name = "Production SQL",
            Host = "PRD-SRV-01",
            Environment = ConnectionEnvironment.Production,
            Tags = ["database", "tier-1"]
        };
        Assert.True(ConnectionSearch.Matches(profile, query));
    }

    [Fact]
    public void SearchRejectsUnrelatedTerms() =>
        Assert.False(ConnectionSearch.Matches(new ConnectionProfile { Name = "Lab", Host = "lab-01" }, "production"));

    [Fact]
    public void ProductionWatermarkIsOptionalAndDefaultsOn()
    {
        Assert.True(new RdpSettings().ShowProductionWatermark);
        Assert.False(new RdpSettings { ShowProductionWatermark = false }.ShowProductionWatermark);
    }

    [Fact]
    public void ConnectionIdentityCanonicalizesIpAddressesAndDnsTrailingDots()
    {
        Assert.Equal(ConnectionIdentity.CreateKey("2001:0db8:0:0:0:0:0:10", 3389), ConnectionIdentity.CreateKey("2001:db8::10", 3389));
        Assert.Equal(ConnectionIdentity.CreateKey("SERVER.EXAMPLE.TEST.", 3389), ConnectionIdentity.CreateKey("server.example.test", 3389));
    }
}
