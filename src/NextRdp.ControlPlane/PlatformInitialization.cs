using Microsoft.EntityFrameworkCore;
using NextRdp.Platform.Infrastructure;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace NextRdp.ControlPlane;

public static class PlatformInitialization
{
    public static async Task InitializePlatformAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await dbContext.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<OpenFgaBootstrapper>().InitializeAsync();
        await SeedDesktopClientAsync(scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>());
    }

    private static async Task SeedDesktopClientAsync(IOpenIddictApplicationManager manager)
    {
        const string clientId = "nextrdp-desktop";
        if (await manager.FindByClientIdAsync(clientId) is not null) return;
        await manager.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "NextRDP Windows client",
            RedirectUris = { new Uri("http://127.0.0.1:43876/callback") },
            PostLogoutRedirectUris = { new Uri("http://127.0.0.1:43876/signed-out") },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.EndSession,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Profile,
                Permissions.Prefixes.Scope + Scopes.OfflineAccess,
                Permissions.Prefixes.Scope + "inventory.read",
                Permissions.Prefixes.Scope + "configuration.read"
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange }
        });
    }
}
