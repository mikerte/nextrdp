using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NextRdp.ControlPlane;
using OpenIddict.Abstractions;

namespace NextRdp.ControlPlane.Tests;

public sealed class CorporateOidcRegistrationTests
{
    [Fact]
    public void ProductionRejectsHttpAuthority()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Authentication:CorporateOidc:Authority"] = "http://identity.example.test"
        });
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddNextRdpAuthentication(configuration, new TestHostEnvironment(Environments.Production)));

        Assert.Contains("must use HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredAuthorityRequiresClientCredentials()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Authentication:CorporateOidc:ClientId"] = string.Empty,
            ["Authentication:CorporateOidc:ClientSecret"] = string.Empty
        });
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddNextRdpAuthentication(configuration, new TestHostEnvironment(Environments.Development)));

        Assert.Contains("client ID and client secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsafeCallbackPathIsRejected()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Authentication:CorporateOidc:CallbackPath"] = "//external.example.test/callback"
        });
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddNextRdpAuthentication(configuration, new TestHostEnvironment(Environments.Development)));

        Assert.Contains("callback path is invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CorporateHandlerUsesFailClosedCodePkceValidation()
    {
        var configuration = Configuration();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNextRdpAuthentication(configuration, new TestHostEnvironment(Environments.Development));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(AuthenticationRegistration.CorporateOidcScheme);

        Assert.Equal(OpenIdConnectResponseType.Code, options.ResponseType);
        Assert.True(options.UsePkce);
        Assert.False(options.SaveTokens);
        Assert.True(options.RequireHttpsMetadata);
        Assert.True(options.TokenValidationParameters.ValidateIssuer);
        Assert.True(options.TokenValidationParameters.ValidateAudience);
        Assert.True(options.TokenValidationParameters.RequireSignedTokens);
        Assert.True(options.TokenValidationParameters.RequireExpirationTime);
        Assert.Equal(TimeSpan.FromMinutes(2), options.TokenValidationParameters.ClockSkew);
        Assert.True(options.ProtocolValidator.RequireNonce);
        Assert.True(options.ProtocolValidator.RequireStateValidation);
        Assert.Contains(OpenIddictConstants.Scopes.OpenId, options.Scope);
        Assert.Contains("groups", options.Scope);
    }

    private static IConfiguration Configuration(IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Authentication:CorporateOidc:Authority"] = "https://identity.example.test",
            ["Authentication:CorporateOidc:ClientId"] = "nextrdp-test-client",
            ["Authentication:CorporateOidc:ClientSecret"] = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            ["Authentication:CorporateOidc:CallbackPath"] = "/signin-corporate",
            ["Authentication:CorporateOidc:SignedOutCallbackPath"] = "/signout-corporate",
            ["Authentication:CorporateOidc:RequireMfaClaim"] = "true",
            ["Authentication:Session:WebIdleMinutes"] = "30",
            ["Authentication:Session:WebAbsoluteMinutes"] = "480",
            ["Authentication:Session:AccessTokenMinutes"] = "30",
            ["Authentication:Session:RefreshTokenMinutes"] = "480",
            ["Authentication:Session:DesktopIdleMinutes"] = "30",
            ["Authentication:Session:DesktopAbsoluteMinutes"] = "480"
        };
        if (overrides is not null)
            foreach (var item in overrides) values[item.Key] = item.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "NextRdp.ControlPlane.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
