using System.Net.Http.Json;
using System.Text.Json.Serialization;
using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Infrastructure;

public sealed class OpenFgaBootstrapper(HttpClient httpClient, OpenFgaOptions options)
{
    private const string StoreName = "NextRDP Phase 2";
    private static readonly string[] Relations = ["administrator", "editor", "publisher", "viewer", "connector", "auditor", "member"];

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var storeId = options.StoreId;
        if (string.IsNullOrWhiteSpace(storeId)) storeId = await FindOrCreateStoreAsync(cancellationToken);
        var modelId = options.AuthorizationModelId;
        if (string.IsNullOrWhiteSpace(modelId)) modelId = await FindOrCreateModelAsync(storeId, cancellationToken);
        options.SetInitializedStore(storeId, modelId);
    }

    private async Task<string> FindOrCreateStoreAsync(CancellationToken cancellationToken)
    {
        using var list = await httpClient.GetAsync($"stores?name={Uri.EscapeDataString(StoreName)}&page_size=10", cancellationToken);
        list.EnsureSuccessStatusCode();
        var stores = await list.Content.ReadFromJsonAsync<StoreList>(cancellationToken: cancellationToken);
        var existing = stores?.Stores.FirstOrDefault(store => store.Name.Equals(StoreName, StringComparison.Ordinal));
        if (existing is not null) return existing.Id;
        using var create = await httpClient.PostAsJsonAsync("stores", new { name = StoreName }, cancellationToken);
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<Store>(cancellationToken: cancellationToken))?.Id
            ?? throw new InvalidOperationException("OpenFGA returned no store identifier.");
    }

    private async Task<string> FindOrCreateModelAsync(string storeId, CancellationToken cancellationToken)
    {
        using var list = await httpClient.GetAsync($"stores/{Uri.EscapeDataString(storeId)}/authorization-models?page_size=1", cancellationToken);
        list.EnsureSuccessStatusCode();
        var models = await list.Content.ReadFromJsonAsync<ModelList>(cancellationToken: cancellationToken);
        var existing = models?.AuthorizationModels.FirstOrDefault();
        if (existing is not null) return existing.Id;

        var directlyRelated = new[] { new { type = "user" }, new { type = "service_account" } };
        var relationDefinitions = Relations.ToDictionary(relation => relation, _ => (object)new { @this = new { } }, StringComparer.Ordinal);
        var relationMetadata = Relations.ToDictionary(relation => relation, _ => (object)new { directly_related_user_types = directlyRelated }, StringComparer.Ordinal);
        var model = new
        {
            schema_version = "1.1",
            type_definitions = new object[]
            {
                new { type = "user" },
                new { type = "service_account" },
                new { type = "platform", relations = relationDefinitions, metadata = new { relations = relationMetadata } },
                new { type = "organization", relations = relationDefinitions, metadata = new { relations = relationMetadata } },
                new { type = "workspace", relations = relationDefinitions, metadata = new { relations = relationMetadata } }
            }
        };
        using var create = await httpClient.PostAsJsonAsync($"stores/{Uri.EscapeDataString(storeId)}/authorization-models", model, cancellationToken);
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<ModelCreated>(cancellationToken: cancellationToken))?.AuthorizationModelId
            ?? throw new InvalidOperationException("OpenFGA returned no authorization model identifier.");
    }

    private sealed record Store(string Id, string Name);
    private sealed record StoreList([property: JsonPropertyName("stores")] Store[] Stores);
    private sealed record Model(string Id);
    private sealed record ModelList([property: JsonPropertyName("authorization_models")] Model[] AuthorizationModels);
    private sealed record ModelCreated([property: JsonPropertyName("authorization_model_id")] string AuthorizationModelId);
}

public sealed class OpenFgaRelationshipService(HttpClient httpClient, OpenFgaOptions options) : IResourceRelationshipService
{
    public Task EnsureRoleAsync(Guid principalId, bool isServiceAccount, WorkspaceRole role, AuthorizationTarget target, CancellationToken cancellationToken = default) =>
        WriteAsync(principalId, isServiceAccount, role, target, true, cancellationToken);

    public Task RemoveRoleAsync(Guid principalId, bool isServiceAccount, WorkspaceRole role, AuthorizationTarget target, CancellationToken cancellationToken = default) =>
        WriteAsync(principalId, isServiceAccount, role, target, false, cancellationToken);

    private async Task WriteAsync(Guid principalId, bool isServiceAccount, WorkspaceRole role, AuthorizationTarget target, bool ensure, CancellationToken cancellationToken)
    {
        options.Validate();
        var objectName = target.ResourceType switch
        {
            "platform" => "platform:root",
            "workspace" when target.WorkspaceId is not null => $"workspace:{target.WorkspaceId.Value:N}",
            "organization" => $"organization:{target.OrganizationId:N}",
            _ => throw new InvalidOperationException("Relationship roles can only be assigned at platform, organization, or workspace scope.")
        };
        var user = isServiceAccount ? $"service_account:{principalId:N}" : $"user:{principalId:N}";
        var tuples = RelationsFor(role).Select(relation => new { user, relation, @object = objectName }).ToArray();
        var operation = new { tuple_keys = tuples, on_duplicate = "ignore" };
        var removal = new { tuple_keys = tuples, on_missing = "ignore" };
        var body = new Dictionary<string, object?>
        {
            [ensure ? "writes" : "deletes"] = ensure ? operation : removal,
            ["authorization_model_id"] = options.AuthorizationModelId
        };
        using var response = await httpClient.PostAsJsonAsync($"stores/{Uri.EscapeDataString(options.StoreId)}/write", body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static string[] RelationsFor(WorkspaceRole role) => role switch
    {
        WorkspaceRole.PlatformAdministrator or WorkspaceRole.OrganizationAdministrator => ["administrator", "editor", "publisher", "viewer", "connector", "auditor", "member"],
        WorkspaceRole.WorkspaceAdministrator => ["editor", "publisher", "viewer", "connector", "auditor", "member"],
        WorkspaceRole.Operator => ["viewer", "connector", "member"],
        WorkspaceRole.Auditor => ["viewer", "auditor"],
        _ => []
    };
}
