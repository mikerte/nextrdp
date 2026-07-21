using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NextRdp.ControlPlane;
using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;
using NextRdp.Platform.Infrastructure;
using OpenIddict.Abstractions;

namespace NextRdp.ControlPlane.Tests;

public sealed class CorporateIdentityPolicyTests
{
    [Fact]
    public void ProductionCannotDisableCorporateMfaEnforcement()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Authentication:CorporateOidc:RequireMfaClaim"] = "false"
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CorporateMfaPolicy.From(configuration, new TestHostEnvironment(Environments.Production)));

        Assert.Contains("cannot be disabled", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CorporateMfaPolicyAcceptsOnlyConfiguredClaimValues()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Authentication:CorporateOidc:RequireMfaClaim"] = "true",
            ["Authentication:CorporateOidc:MfaClaimType"] = "acr",
            ["Authentication:CorporateOidc:MfaClaimValues:0"] = "urn:approved:mfa"
        });
        var policy = CorporateMfaPolicy.From(configuration, new TestHostEnvironment(Environments.Production));
        var accepted = Principal(new Claim("acr", "URN:APPROVED:MFA"));
        var rejected = Principal(new Claim("amr", "mfa"));

        Assert.True(policy.Evaluate(accepted, false));
        var exception = Assert.Throws<PlatformProblemException>(() => policy.Evaluate(rejected, false));
        Assert.Equal(403, exception.StatusCode);
    }

    [Fact]
    public async Task UnmappedCorporateRoleClaimDoesNotGrantProductRole()
    {
        await using var context = CreateContext();
        var provisioner = CreateProvisioner(context, Configuration(), new RecordingRelationships());
        var principal = Principal(
            new Claim(OpenIddictConstants.Claims.Issuer, "https://identity.example.test"),
            new Claim(OpenIddictConstants.Claims.Subject, "unmapped-user"),
            new Claim(OpenIddictConstants.Claims.Name, "Unmapped user"),
            new Claim(OpenIddictConstants.Claims.AuthenticationMethodReference, "mfa"),
            new Claim(OpenIddictConstants.Claims.Role, WorkspaceRole.PlatformAdministrator.ToString()));

        var result = await provisioner.ProvisionAsync(principal);

        Assert.Empty(result.Roles);
    }

    [Fact]
    public async Task ConfiguredGroupMappingGrantsOnlyMappedRole()
    {
        await using var context = CreateContext();
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Authentication:CorporateOidc:GroupRoleMappings:corporate-operators"] = WorkspaceRole.Operator.ToString()
        });
        var provisioner = CreateProvisioner(context, configuration, new RecordingRelationships());
        var principal = Principal(
            new Claim(OpenIddictConstants.Claims.Issuer, "https://identity.example.test"),
            new Claim(OpenIddictConstants.Claims.Subject, "mapped-user"),
            new Claim(OpenIddictConstants.Claims.Name, "Mapped user"),
            new Claim(OpenIddictConstants.Claims.AuthenticationMethodReference, "mfa"),
            new Claim("groups", "corporate-operators"),
            new Claim(OpenIddictConstants.Claims.Role, WorkspaceRole.PlatformAdministrator.ToString()));

        var result = await provisioner.ProvisionAsync(principal);

        Assert.Equal([WorkspaceRole.Operator], result.Roles);
    }

    [Fact]
    public async Task MissingCorporateMfaIsRejectedBeforeUserProvisioning()
    {
        await using var context = CreateContext();
        var provisioner = CreateProvisioner(context, Configuration(), new RecordingRelationships());
        var principal = Principal(
            new Claim(OpenIddictConstants.Claims.Issuer, "https://identity.example.test"),
            new Claim(OpenIddictConstants.Claims.Subject, "no-mfa-user"),
            new Claim(OpenIddictConstants.Claims.Name, "No MFA user"));

        var exception = await Assert.ThrowsAsync<PlatformProblemException>(() => provisioner.ProvisionAsync(principal));

        Assert.Equal(403, exception.StatusCode);
        Assert.Empty(await context.Users.ToListAsync());
    }

    private static FederatedUserProvisioner CreateProvisioner(PlatformDbContext context, IConfiguration configuration, IResourceRelationshipService relationships) =>
        new(context, configuration, TimeProvider.System, relationships,
            CorporateMfaPolicy.From(configuration, new TestHostEnvironment(Environments.Production)));

    private static PlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase($"nextrdp-corporate-policy-{Guid.NewGuid():N}")
            .Options;
        return new PlatformDbContext(options);
    }

    private static IConfiguration Configuration(IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Authentication:CorporateOidc:Authority"] = "https://identity.example.test",
            ["Authentication:CorporateOidc:RequireMfaClaim"] = "true",
            ["Authentication:CorporateOidc:MfaClaimType"] = OpenIddictConstants.Claims.AuthenticationMethodReference,
            ["Authentication:CorporateOidc:MfaClaimValues:0"] = "mfa"
        };
        if (overrides is not null)
            foreach (var item in overrides) values[item.Key] = item.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "CorporateOidc"));

    private sealed class RecordingRelationships : IResourceRelationshipService
    {
        public Task EnsureRoleAsync(Guid principalId, bool isServiceAccount, WorkspaceRole role, AuthorizationTarget target, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveRoleAsync(Guid principalId, bool isServiceAccount, WorkspaceRole role, AuthorizationTarget target, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "NextRdp.ControlPlane.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
