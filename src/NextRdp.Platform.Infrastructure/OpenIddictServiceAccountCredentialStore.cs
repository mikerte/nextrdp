using NextRdp.Platform.Application;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace NextRdp.Platform.Infrastructure;

public sealed class OpenIddictServiceAccountCredentialStore(IOpenIddictApplicationManager applicationManager) : IServiceAccountCredentialStore
{
    public async Task StoreAsync(string clientId, string clientSecret, IReadOnlyList<string> scopes, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken = default)
    {
        if (await applicationManager.FindByClientIdAsync(clientId, cancellationToken) is not null)
            throw PlatformProblemException.Conflict("A token client with this identifier already exists.");

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            ClientType = ClientTypes.Confidential,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = clientId,
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.ClientCredentials
            }
        };
        foreach (var scope in scopes) descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
        if (expiresAtUtc is not null) descriptor.Properties["nextrdp_expires_at"] = System.Text.Json.JsonSerializer.SerializeToElement(expiresAtUtc.Value);
        await applicationManager.CreateAsync(descriptor, cancellationToken);
    }

    public async Task RevokeAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var application = await applicationManager.FindByClientIdAsync(clientId, cancellationToken);
        if (application is not null) await applicationManager.DeleteAsync(application, cancellationToken);
    }

    public async Task RotateAsync(string clientId, string clientSecret, IReadOnlyList<string> scopes, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken = default)
    {
        var application = await applicationManager.FindByClientIdAsync(clientId, cancellationToken)
            ?? throw PlatformProblemException.NotFound("service account token client");
        var descriptor = new OpenIddictApplicationDescriptor();
        await applicationManager.PopulateAsync(descriptor, application, cancellationToken);
        descriptor.ClientSecret = clientSecret;
        descriptor.Permissions.Clear();
        descriptor.Permissions.Add(Permissions.Endpoints.Token);
        descriptor.Permissions.Add(Permissions.GrantTypes.ClientCredentials);
        foreach (var scope in scopes) descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
        if (expiresAtUtc is null) descriptor.Properties.Remove("nextrdp_expires_at");
        else descriptor.Properties["nextrdp_expires_at"] = System.Text.Json.JsonSerializer.SerializeToElement(expiresAtUtc.Value);
        await applicationManager.UpdateAsync(application, descriptor, cancellationToken);
    }
}
