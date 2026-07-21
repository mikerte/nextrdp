using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NextRdp.Platform.Infrastructure;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;

namespace NextRdp.ControlPlane;

public sealed record ProductSessionOptions(int WebIdleMinutes, int WebAbsoluteMinutes, int AccessTokenMinutes, int RefreshTokenMinutes, int DesktopIdleMinutes, int DesktopAbsoluteMinutes)
{
    public static ProductSessionOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection("Authentication:Session");
        var options = new ProductSessionOptions(
            section.GetValue("WebIdleMinutes", 30), section.GetValue("WebAbsoluteMinutes", 480), section.GetValue("AccessTokenMinutes", 30),
            section.GetValue("RefreshTokenMinutes", 480), section.GetValue("DesktopIdleMinutes", 30), section.GetValue("DesktopAbsoluteMinutes", 480));
        if (options.WebIdleMinutes is < 5 or > 1440 || options.WebAbsoluteMinutes < options.WebIdleMinutes || options.WebAbsoluteMinutes > 10080 ||
            options.AccessTokenMinutes is < 5 or > 120 || options.RefreshTokenMinutes < options.AccessTokenMinutes || options.RefreshTokenMinutes > 10080 ||
            options.DesktopIdleMinutes is < 5 or > 1440 || options.DesktopAbsoluteMinutes < options.DesktopIdleMinutes || options.DesktopAbsoluteMinutes > 10080)
            throw new InvalidOperationException("Authentication session timeout configuration is outside the supported Phase 2 bounds.");
        return options;
    }
}

public static class AuthenticationRegistration
{
    public const string SmartScheme = "NextRdpSmart";
    public const string FederationCookieScheme = "NextRdpFederation";
    public const string CorporateOidcScheme = "CorporateOidc";

    public static IServiceCollection AddNextRdpAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var sessionOptions = ProductSessionOptions.From(configuration);
        services.AddSingleton(sessionOptions);
        services.AddSingleton(CorporateMfaPolicy.From(configuration, environment));
        var corporate = configuration.GetSection("Authentication:CorporateOidc");
        var authority = corporate["Authority"];
        var developmentIdentityEnabled = configuration.GetValue("Authentication:DevelopmentIdentity:Enabled", false);
        if (!environment.IsDevelopment() && developmentIdentityEnabled)
            throw new InvalidOperationException("The development identity cannot be enabled outside the Development environment.");
        if (!environment.IsDevelopment() && string.IsNullOrWhiteSpace(authority))
            throw new InvalidOperationException("A corporate OIDC authority is required outside the Development environment.");
        if (!string.IsNullOrWhiteSpace(authority)) ValidateCorporateOidcConfiguration(corporate, environment);

        var authentication = services.AddAuthentication(options =>
        {
            options.DefaultScheme = SmartScheme;
            options.DefaultAuthenticateScheme = SmartScheme;
            options.DefaultChallengeScheme = FederationCookieScheme;
        });
        authentication.AddPolicyScheme(SmartScheme, SmartScheme, options =>
        {
            options.ForwardDefaultSelector = context => context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme
                : FederationCookieScheme;
        });
        authentication.AddCookie(FederationCookieScheme, options =>
        {
            options.Cookie.Name = environment.IsDevelopment() ? "NextRdp-Session-Development" : "__Host-NextRdp-Session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.LoginPath = "/auth/login";
            options.LogoutPath = "/auth/logout";
            options.SlidingExpiration = false;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(sessionOptions.WebAbsoluteMinutes);
            options.Events.OnSigningIn = context =>
            {
                var now = DateTimeOffset.UtcNow;
                var identity = context.Principal?.Identities.FirstOrDefault();
                var previousActivity = identity?.FindFirst("nextrdp_last_activity");
                if (previousActivity is not null)
                {
                    identity!.TryRemoveClaim(previousActivity);
                }
                identity?.AddClaim(new System.Security.Claims.Claim("nextrdp_last_activity", now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
                context.Properties.IssuedUtc ??= now;
                context.Properties.ExpiresUtc = context.Properties.IssuedUtc.Value.AddMinutes(sessionOptions.WebAbsoluteMinutes);
                return Task.CompletedTask;
            };
            options.Events.OnValidatePrincipal = async context =>
            {
                var now = DateTimeOffset.UtcNow;
                var issued = context.Properties.IssuedUtc ?? now;
                var activityClaim = context.Principal?.FindFirst("nextrdp_last_activity")?.Value;
                var lastActivity = long.TryParse(activityClaim, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds) : issued;
                if (now - lastActivity > TimeSpan.FromMinutes(sessionOptions.WebIdleMinutes) || now - issued > TimeSpan.FromMinutes(sessionOptions.WebAbsoluteMinutes))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(FederationCookieScheme);
                    return;
                }
                if (now - lastActivity > TimeSpan.FromMinutes(1) && context.Principal?.Identity is System.Security.Claims.ClaimsIdentity identity)
                {
                    var old = identity.FindFirst("nextrdp_last_activity");
                    if (old is not null) identity.TryRemoveClaim(old);
                    identity.AddClaim(new System.Security.Claims.Claim("nextrdp_last_activity", now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    context.ShouldRenew = true;
                }
            };
            options.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal) ||
                    context.Request.Path.StartsWithSegments("/auth/me", StringComparison.Ordinal) ||
                    context.Request.Path.StartsWithSegments("/auth/csrf", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }
                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal) || context.Request.Path.StartsWithSegments("/auth", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }
                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
        });
        if (!string.IsNullOrWhiteSpace(authority))
        {
            authentication.AddOpenIdConnect(CorporateOidcScheme, options =>
            {
                options.SignInScheme = FederationCookieScheme;
                options.Authority = authority;
                options.ClientId = corporate["ClientId"] ?? throw new InvalidOperationException("Corporate OIDC client ID is required.");
                options.ClientSecret = corporate["ClientSecret"] ?? throw new InvalidOperationException("Corporate OIDC client secret is required.");
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.MapInboundClaims = false;
                options.CallbackPath = corporate["CallbackPath"] ?? "/signin-corporate";
                options.SignedOutCallbackPath = corporate["SignedOutCallbackPath"] ?? "/signout-corporate";
                options.Scope.Clear();
                options.Scope.Add(OpenIddictConstants.Scopes.OpenId);
                options.Scope.Add(OpenIddictConstants.Scopes.Profile);
                options.Scope.Add(OpenIddictConstants.Scopes.Email);
                options.Scope.Add("groups");
                options.TokenValidationParameters.NameClaimType = OpenIddictConstants.Claims.Name;
                options.TokenValidationParameters.RoleClaimType = OpenIddictConstants.Claims.Role;
                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.ValidateAudience = true;
                options.TokenValidationParameters.RequireSignedTokens = true;
                options.TokenValidationParameters.RequireExpirationTime = true;
                options.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(2);
                options.ProtocolValidator.RequireNonce = true;
                options.ProtocolValidator.RequireStateValidation = true;
            });
        }

        services.AddAuthorization(options =>
        {
            options.AddPolicy("NextRdpApi", policy =>
            {
                policy.AddAuthenticationSchemes(SmartScheme);
                policy.RequireAuthenticatedUser();
            });
        });

        services.AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<PlatformDbContext>().ReplaceDefaultEntities<Guid>())
            .AddServer(options =>
            {
                options.SetAuthorizationEndpointUris("connect/authorize")
                    .SetTokenEndpointUris("connect/token")
                    .SetEndSessionEndpointUris("connect/logout");
                options.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow().AllowClientCredentialsFlow();
                options.RequireProofKeyForCodeExchange();
                options.RegisterScopes(
                    OpenIddictConstants.Scopes.OpenId,
                    OpenIddictConstants.Scopes.Profile,
                    OpenIddictConstants.Scopes.Email,
                    OpenIddictConstants.Scopes.OfflineAccess,
                    "inventory.read",
                    "inventory.write",
                    "configuration.read",
                    "configuration.write",
                    "audit.read",
                    "bulk.write");
                options.SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(5));
                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(sessionOptions.AccessTokenMinutes));
                options.SetRefreshTokenLifetime(TimeSpan.FromMinutes(sessionOptions.RefreshTokenMinutes));
                ConfigureCredentials(options, configuration, environment);
                var aspNetCore = options.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough()
                    .EnableStatusCodePagesIntegration();
                if (environment.IsDevelopment()) aspNetCore.DisableTransportSecurityRequirement();
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });
        return services;
    }

    private static void ValidateCorporateOidcConfiguration(IConfigurationSection corporate, IHostEnvironment environment)
    {
        var authorityText = corporate["Authority"];
        if (!Uri.TryCreate(authorityText, UriKind.Absolute, out var authority) || !string.IsNullOrEmpty(authority.UserInfo) || !string.IsNullOrEmpty(authority.Fragment))
            throw new InvalidOperationException("The corporate OIDC authority must be an absolute URI without user information or a fragment.");
        if (!environment.IsDevelopment() && !authority.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The corporate OIDC authority must use HTTPS outside the Development environment.");
        if (string.IsNullOrWhiteSpace(corporate["ClientId"]) || string.IsNullOrWhiteSpace(corporate["ClientSecret"]))
            throw new InvalidOperationException("Corporate OIDC client ID and client secret are required when an authority is configured.");
        ValidateCallbackPath(corporate["CallbackPath"] ?? "/signin-corporate", "callback");
        ValidateCallbackPath(corporate["SignedOutCallbackPath"] ?? "/signout-corporate", "signed-out callback");
    }

    private static void ValidateCallbackPath(string value, string label)
    {
        if (value.Length is < 2 or > 256 || value[0] != '/' || value.StartsWith("//", StringComparison.Ordinal) || value.Contains('\\') || value.Contains('?') || value.Contains('#'))
            throw new InvalidOperationException($"The corporate OIDC {label} path is invalid.");
    }

    private static void ConfigureCredentials(OpenIddictServerBuilder options, IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            options.AddDevelopmentEncryptionCertificate().AddDevelopmentSigningCertificate();
            return;
        }
        var signing = LoadCertificate(configuration["Security:SigningCertificatePath"], configuration["Security:SigningCertificatePassword"], "signing");
        var encryption = LoadCertificate(configuration["Security:EncryptionCertificatePath"], configuration["Security:EncryptionCertificatePassword"], "encryption");
        options.AddSigningCertificate(signing).AddEncryptionCertificate(encryption);
    }

    private static X509Certificate2 LoadCertificate(string? path, string? password, string purpose)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException($"A production {purpose} certificate path and password are required.");
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        if (!certificate.HasPrivateKey || DateTimeOffset.UtcNow < certificate.NotBefore || DateTimeOffset.UtcNow >= certificate.NotAfter)
            throw new InvalidOperationException($"The production {purpose} certificate is invalid, expired, or has no private key.");
        return certificate;
    }
}
