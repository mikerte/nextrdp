using System.Net.Http.Json;
using System.Text.Json.Serialization;
using NextRdp.Platform.Application;

namespace NextRdp.Platform.Infrastructure;

public sealed class OpenFgaOptions(Uri baseAddress, string storeId, string? authorizationModelId = null)
{
    public Uri BaseAddress { get; } = baseAddress;
    public string StoreId { get; private set; } = storeId;
    public string? AuthorizationModelId { get; private set; } = authorizationModelId;

    public void SetInitializedStore(string storeId, string authorizationModelId)
    {
        StoreId = storeId;
        AuthorizationModelId = authorizationModelId;
        Validate();
    }

    public void Validate()
    {
        if (!BaseAddress.IsAbsoluteUri || BaseAddress.Scheme is not ("http" or "https")) throw new InvalidOperationException("OpenFGA base address must be an absolute HTTP(S) URI.");
        if (string.IsNullOrWhiteSpace(StoreId) || StoreId.Length > 128 || StoreId.Any(char.IsControl)) throw new InvalidOperationException("OpenFGA store ID is required.");
        if (AuthorizationModelId?.Length > 128 || AuthorizationModelId?.Any(char.IsControl) == true) throw new InvalidOperationException("OpenFGA authorization model ID is invalid.");
    }
}

public sealed class OpenFgaAuthorizationService(HttpClient httpClient, OpenFgaOptions options) : IResourceAuthorizationService
{
    public async Task<AuthorizationDecision> AuthorizeAsync(PlatformActor actor, PlatformAction action, AuthorizationTarget target, CancellationToken cancellationToken = default)
    {
        if (!actor.IsAuthenticated) return AuthorizationDecision.Deny("Authentication is required.");
        options.Validate();
        var resource = target.ResourceType == "platform"
            ? "platform:root"
            : target.WorkspaceId is not null
                ? $"workspace:{target.WorkspaceId.Value:N}"
                : $"organization:{target.OrganizationId:N}";
        var tuple = new OpenFgaTuple(
            actor.IsServiceAccount ? $"service_account:{actor.PrincipalId:N}" : $"user:{actor.PrincipalId:N}",
            Relation(action),
            resource);
        var request = new OpenFgaCheckRequest(tuple, options.AuthorizationModelId);
        try
        {
            using var response = await httpClient.PostAsJsonAsync($"stores/{Uri.EscapeDataString(options.StoreId)}/check", request, cancellationToken);
            if (!response.IsSuccessStatusCode) return AuthorizationDecision.Deny("The relationship authorization service did not return a valid decision.");
            var body = await response.Content.ReadFromJsonAsync<OpenFgaCheckResponse>(cancellationToken: cancellationToken);
            return body?.Allowed == true
                ? AuthorizationDecision.Allow("Allowed by the scoped resource relationship.", "openfga")
                : AuthorizationDecision.Deny("The scoped resource relationship does not allow this action.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or NotSupportedException or System.Text.Json.JsonException)
        {
            return AuthorizationDecision.Deny("The relationship authorization service is unavailable or returned an invalid decision.");
        }
    }

    private static string Relation(PlatformAction action) => action switch
    {
        PlatformAction.ReadOrganization or PlatformAction.ReadWorkspace or PlatformAction.ReadInventory or PlatformAction.ReadConfiguration or PlatformAction.RunGatewayDiagnostics => "viewer",
        PlatformAction.Connect => "connector",
        PlatformAction.ReadAudit => "auditor",
        PlatformAction.ManagePersonalInventory or PlatformAction.ManageProductivity => "member",
        PlatformAction.ManageWorkspace or PlatformAction.ManageSharedInventory or PlatformAction.ManageConfiguration or PlatformAction.RunBulkAutomation => "editor",
        PlatformAction.PublishConfiguration => "publisher",
        PlatformAction.ManageOrganization or PlatformAction.ManageMemberships or PlatformAction.ManageServiceAccounts or PlatformAction.CreateBackup or PlatformAction.RestoreBackup => "administrator",
        _ => "denied"
    };

    private sealed record OpenFgaCheckRequest(
        [property: JsonPropertyName("tuple_key")] OpenFgaTuple TupleKey,
        [property: JsonPropertyName("authorization_model_id")] string? AuthorizationModelId);

    private sealed record OpenFgaTuple(
        [property: JsonPropertyName("user")] string User,
        [property: JsonPropertyName("relation")] string Relation,
        [property: JsonPropertyName("object")] string Object);

    private sealed record OpenFgaCheckResponse([property: JsonPropertyName("allowed")] bool Allowed);
}
