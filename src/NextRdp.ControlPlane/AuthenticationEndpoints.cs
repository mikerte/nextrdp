using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;
using NextRdp.Platform.Infrastructure;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace NextRdp.ControlPlane;

public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/auth/login", LoginAsync).AllowAnonymous();
        endpoints.MapGet("/auth/development-signin", DevelopmentSignInAsync).AllowAnonymous();
        endpoints.MapGet("/auth/me", MeAsync).RequireAuthorization("NextRdpApi");
        endpoints.MapGet("/auth/csrf", CsrfAsync).RequireAuthorization("NextRdpApi");
        endpoints.MapPost("/auth/logout", (Delegate)LogoutAsync).RequireAuthorization("NextRdpApi").AddEndpointFilter<CookieAntiforgeryFilter>();
        endpoints.MapPost("/auth/global-logout", (Delegate)GlobalLogoutAsync).RequireAuthorization("NextRdpApi").AddEndpointFilter<CookieAntiforgeryFilter>();
        endpoints.MapMethods("/connect/authorize", [HttpMethods.Get, HttpMethods.Post], (Delegate)AuthorizeAsync).AllowAnonymous();
        endpoints.MapPost("/connect/token", (Delegate)ExchangeAsync).AllowAnonymous();
        endpoints.MapMethods("/connect/logout", [HttpMethods.Get, HttpMethods.Post], (Delegate)EndSessionAsync).AllowAnonymous();
        return endpoints;
    }

    private static IResult LoginAsync(HttpContext context, IConfiguration configuration, IHostEnvironment environment, string? returnUrl)
    {
        var safeReturnUrl = SafeReturnUrl(returnUrl);
        if (!string.IsNullOrWhiteSpace(configuration["Authentication:CorporateOidc:Authority"]))
            return Results.Challenge(new AuthenticationProperties { RedirectUri = safeReturnUrl }, [AuthenticationRegistration.CorporateOidcScheme]);
        if (environment.IsDevelopment() && configuration.GetValue("Authentication:DevelopmentIdentity:Enabled", false))
            return Results.Redirect($"/auth/development-signin?returnUrl={Uri.EscapeDataString(safeReturnUrl)}");
        return Results.Problem(statusCode: 503, title: "Corporate sign-in is not configured", detail: "An administrator must configure a standards-compliant OIDC provider.");
    }

    private static async Task<IResult> DevelopmentSignInAsync(HttpContext context, IConfiguration configuration, IHostEnvironment environment, string? returnUrl)
    {
        if (!environment.IsDevelopment() || !configuration.GetValue("Authentication:DevelopmentIdentity:Enabled", false)) return Results.NotFound();
        var displayName = configuration["Authentication:DevelopmentIdentity:DisplayName"] ?? "NextRDP Development Administrator";
        var identity = new ClaimsIdentity(AuthenticationRegistration.FederationCookieScheme, Claims.Name, Claims.Role);
        identity.AddClaims(
        [
            new Claim(Claims.Issuer, "https://development.nextrdp.local"),
            new Claim(Claims.Subject, "development-administrator"),
            new Claim(Claims.Name, displayName),
            new Claim(Claims.Role, WorkspaceRole.PlatformAdministrator.ToString()),
            new Claim(Claims.AuthenticationMethodReference, "mfa"),
            new Claim("nextrdp_development_identity", "true")
        ]);
        await context.SignInAsync(AuthenticationRegistration.FederationCookieScheme, new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
        });
        return Results.Redirect(SafeReturnUrl(returnUrl));
    }

    private static async Task<IResult> MeAsync(HttpContext context, IPlatformActorAccessor actorAccessor, CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetAsync(context, cancellationToken);
        return Results.Ok(new
        {
            actor.PrincipalId,
            actor.OrganizationId,
            roles = actor.Roles.Order().Select(role => role.ToString()),
            actor.Groups,
            actor.MfaSatisfied,
            actor.SessionVersion,
            actor.IsServiceAccount,
            scopes = actor.Scopes is null ? [] : actor.Scopes.Order(StringComparer.Ordinal).ToArray()
        });
    }

    private static async Task<IResult> CsrfAsync(HttpContext context, IAntiforgery antiforgery, IPlatformActorAccessor actorAccessor, CancellationToken cancellationToken)
    {
        _ = await actorAccessor.GetAsync(context, cancellationToken);
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { requestToken = tokens.RequestToken });
    }

    private static async Task<IResult> LogoutAsync(HttpContext context)
    {
        await context.SignOutAsync(AuthenticationRegistration.FederationCookieScheme);
        return Results.NoContent();
    }

    private static async Task<IResult> GlobalLogoutAsync(HttpContext context, IPlatformActorAccessor actors, IPlatformSessionRevocationService sessions, CancellationToken cancellationToken)
    {
        await sessions.RevokeSelfAsync(await actors.GetAsync(context, cancellationToken), cancellationToken);
        await context.SignOutAsync(AuthenticationRegistration.FederationCookieScheme);
        return Results.NoContent();
    }

    private static async Task<IResult> AuthorizeAsync(HttpContext context, IFederatedUserProvisioner provisioner, PlatformDbContext dbContext, ProductSessionOptions sessionOptions, CancellationToken cancellationToken)
    {
        var request = context.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("OpenID Connect authorization request is unavailable.");
        var authentication = await context.AuthenticateAsync(AuthenticationRegistration.FederationCookieScheme);
        if (!authentication.Succeeded || authentication.Principal is null)
        {
            var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
            return Results.Redirect($"/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        var provisioned = await provisioner.ProvisionAsync(authentication.Principal, cancellationToken);
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        AddTokenClaim(identity, Claims.Subject, provisioned.User.Id.ToString("N"));
        AddTokenClaim(identity, "nextrdp_user_id", provisioned.User.Id.ToString("D"));
        AddTokenClaim(identity, Claims.Name, provisioned.User.DisplayName);
        if (!string.IsNullOrWhiteSpace(provisioned.User.EmailHint)) AddTokenClaim(identity, Claims.Email, provisioned.User.EmailHint);
        if (provisioned.OrganizationId is not null) AddTokenClaim(identity, "nextrdp_org_id", provisioned.OrganizationId.Value.ToString("D"));
        AddTokenClaim(identity, "nextrdp_session_version", provisioned.User.SessionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var now = DateTimeOffset.UtcNow;
        var productSession = new ProductSession
        {
            UserId = provisioned.User.Id,
            SessionVersion = provisioned.User.SessionVersion,
            StartedAtUtc = now,
            LastActivityAtUtc = now,
            IdleExpiresAtUtc = now.AddMinutes(sessionOptions.DesktopIdleMinutes),
            AbsoluteExpiresAtUtc = now.AddMinutes(sessionOptions.DesktopAbsoluteMinutes),
            IdleTimeoutMinutes = sessionOptions.DesktopIdleMinutes,
            ClientId = request.ClientId ?? string.Empty
        };
        dbContext.ProductSessions.Add(productSession);
        await dbContext.SaveChangesAsync(cancellationToken);
        AddTokenClaim(identity, "nextrdp_session_id", productSession.Id.ToString("D"));
        foreach (var role in provisioned.Roles) AddTokenClaim(identity, Claims.Role, role.ToString());
        foreach (var group in provisioned.Groups) AddTokenClaim(identity, "groups", group);
        if (provisioned.MfaSatisfied) AddTokenClaim(identity, Claims.AuthenticationMethodReference, "mfa");
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());
        principal.SetResources("nextrdp_api");
        return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> ExchangeAsync(HttpContext context, PlatformDbContext dbContext, IOpenIddictApplicationManager applicationManager, CancellationToken cancellationToken)
    {
        var request = context.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("OpenID Connect token request is unavailable.");
        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            if (!result.Succeeded || result.Principal is null)
                return OAuthForbid(Errors.InvalidGrant, "The authorization code or refresh token is invalid or expired.");
            if (!Guid.TryParse(result.Principal.FindFirst("nextrdp_user_id")?.Value, out var userId))
                return OAuthForbid(Errors.InvalidGrant, "The product identity is no longer valid.");
            var user = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == userId, cancellationToken);
            if (user is null || user.Status != MembershipStatus.Active)
                return OAuthForbid(Errors.InvalidGrant, "The product identity is suspended, revoked, or unavailable.");
            if (long.TryParse(result.Principal.FindFirst("nextrdp_session_version")?.Value, out var tokenVersion) && tokenVersion != user.SessionVersion)
                return OAuthForbid(Errors.InvalidGrant, "The product session was revoked. Sign in again.");
            if (!Guid.TryParse(result.Principal.FindFirst("nextrdp_session_id")?.Value, out var sessionId))
                return OAuthForbid(Errors.InvalidGrant, "The product session is missing. Sign in again.");
            var session = await dbContext.ProductSessions.SingleOrDefaultAsync(entity => entity.Id == sessionId && entity.UserId == userId, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            if (session is null || !session.IsActive(now, user.SessionVersion))
                return OAuthForbid(Errors.InvalidGrant, "The product session expired or was revoked. Sign in again.");
            session.RecordActivity(now);
            await dbContext.SaveChangesAsync(cancellationToken);
            result.Principal.SetClaim("nextrdp_session_version", user.SessionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var interactiveScopes = request.GetScopes();
            result.Principal.SetScopes(interactiveScopes.Any() ? interactiveScopes : result.Principal.GetScopes());
            result.Principal.SetResources("nextrdp_api");
            return Results.SignIn(result.Principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }
        if (!request.IsClientCredentialsGrantType())
            return OAuthForbid(Errors.UnsupportedGrantType, "Only client-credentials, authorization-code, and refresh-token grants are supported at this endpoint.");

        var application = await applicationManager.FindByClientIdAsync(request.ClientId!, cancellationToken)
            ?? throw new InvalidOperationException("The validated token client no longer exists.");
        var clientId = await applicationManager.GetClientIdAsync(application, cancellationToken)
            ?? throw new InvalidOperationException("The validated token client has no identifier.");
        var account = await dbContext.ServiceAccounts.AsNoTracking().SingleOrDefaultAsync(entity => entity.ClientId == clientId, cancellationToken);
        if (account is null || account.RevokedAtUtc is not null || account.ExpiresAtUtc is not null && account.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return OAuthForbid(Errors.InvalidClient, "The service account is revoked, expired, or unavailable.");
        var allowedScopes = JsonSerializer.Deserialize<string[]>(account.ScopesJson) ?? [];
        var requestedScopes = request.GetScopes().ToHashSet(StringComparer.Ordinal);
        if (!requestedScopes.IsSubsetOf(allowedScopes)) return OAuthForbid(Errors.InvalidScope, "One or more requested scopes are not assigned to this service account.");

        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        AddTokenClaim(identity, Claims.Subject, account.Id.ToString("N"));
        AddTokenClaim(identity, Claims.Name, account.Name);
        AddTokenClaim(identity, "nextrdp_user_id", account.Id.ToString("D"));
        AddTokenClaim(identity, "nextrdp_service_account", "true");
        AddTokenClaim(identity, "nextrdp_org_id", account.OrganizationId.ToString("D"));
        if (account.WorkspaceId is not null) AddTokenClaim(identity, "nextrdp_workspace_id", account.WorkspaceId.Value.ToString("D"));
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(requestedScopes);
        principal.SetResources("nextrdp_api");
        return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> EndSessionAsync(HttpContext context)
    {
        await context.SignOutAsync(AuthenticationRegistration.FederationCookieScheme);
        return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }

    private static IResult OAuthForbid(string error, string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
        }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    private static void AddTokenClaim(ClaimsIdentity identity, string type, string value) =>
        identity.AddClaim(new Claim(type, value).SetDestinations(Destinations.AccessToken, Destinations.IdentityToken));

    private static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && returnUrl[0] == '/' && !returnUrl.StartsWith("//", StringComparison.Ordinal) && !returnUrl.Contains('\\')
            ? returnUrl
            : "/";
}
