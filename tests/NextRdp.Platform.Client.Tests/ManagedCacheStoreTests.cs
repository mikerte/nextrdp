using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using System.Net.Http.Json;
using NextRdp.Core;
using NextRdp.Platform.Application;
using NextRdp.Platform.Client;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Client.Tests;

public sealed class ManagedCacheStoreTests
{
    static ManagedCacheStoreTests() => SQLitePCL.Batteries_V2.Init();

    [Fact]
    public async Task CachePreservesScopedMetadataPolicyAndProductivityWithoutCredentials()
    {
        using var directory = new TemporaryDirectory();
        var database = Path.Combine(directory.Path, "managed-cache.db");
        var store = new ManagedCacheStore(database);
        await store.InitializeAsync();
        var selection = Selection();
        await store.SelectAsync(selection);
        var connection = Connection(selection, "credential-sentinel-must-not-exist");
        var response = new SyncResponse(
            [new SyncItem("connection", connection.Id, SyncChangeKind.Upsert, 3, JsonSerializer.SerializeToElement(connection))],
            "cursor-3", DateTimeOffset.UtcNow, "0.2.0", false, 7);
        await store.ApplyAsync(selection, response);
        var policy = new EffectivePolicy(selection.OrganizationId, selection.WorkspaceId, null, connection.Id, 9,
            new Dictionary<string, EffectivePolicyField> { ["rdp.requireNla"] = new("rdp.requireNla", "true", ConfigurationScopeType.Workspace, selection.WorkspaceId, true) }, []);
        await store.SaveEffectivePolicyAsync(selection, policy);
        var userId = Guid.CreateVersion7();
        var productivity = new ManagedProductivitySnapshot([], [], new UserWorkspaceStateDto(selection.OrganizationId, selection.WorkspaceId, userId, [], new LayoutPreferencesDto(320, "name", "system", true), [], [], 1, DateTimeOffset.UtcNow));
        await store.SaveProductivityAsync(selection, productivity);

        var loaded = await store.LoadAsync(selection, false, "offline");
        var policies = await store.LoadEffectivePoliciesAsync(selection);
        var cachedProductivity = await store.LoadProductivityAsync(selection);
        var revisions = await store.KnownRevisionsAsync(selection.OrganizationId, selection.WorkspaceId);

        Assert.Single(loaded.Connections);
        Assert.True(loaded.Status.IsReadOnly);
        Assert.Equal(9, policies[connection.Id].Revision);
        Assert.NotNull(cachedProductivity);
        Assert.Equal(3, revisions[connection.Id]);
        Assert.DoesNotContain("credential-sentinel-must-not-exist", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(database)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CacheCoalescesRepeatedChangesForANewResourceWithinOneSyncPage()
    {
        using var directory = new TemporaryDirectory();
        var store = new ManagedCacheStore(Path.Combine(directory.Path, "managed-cache.db"));
        await store.InitializeAsync();
        var selection = Selection();
        await store.SelectAsync(selection);
        var connection = Connection(selection, string.Empty);
        var created = connection with { Name = "Created profile", Revision = 1 };
        var updated = connection with { Name = "Separate window profile", Revision = 2 };

        await store.ApplyAsync(selection, new SyncResponse([
            new SyncItem("connection", connection.Id, SyncChangeKind.Upsert, 1, JsonSerializer.SerializeToElement(created)),
            new SyncItem("connection", connection.Id, SyncChangeKind.Upsert, 2, JsonSerializer.SerializeToElement(updated))
        ], "cursor-2", DateTimeOffset.UtcNow, "0.2.0", false, 1));

        var loaded = await store.LoadAsync(selection, true, "online");
        var cached = Assert.Single(loaded.Connections);
        Assert.Equal("Separate window profile", cached.Name);
        Assert.Equal(2, (await store.KnownRevisionsAsync(selection.OrganizationId, selection.WorkspaceId))[connection.Id]);
    }

    [Fact]
    public async Task AccessRevocationPurgesAllCachedWorkspaceResources()
    {
        using var directory = new TemporaryDirectory();
        var store = new ManagedCacheStore(Path.Combine(directory.Path, "managed-cache.db"));
        await store.InitializeAsync();
        var selection = Selection();
        await store.SelectAsync(selection);
        var connection = Connection(selection, string.Empty);
        await store.ApplyAsync(selection, new SyncResponse([new SyncItem("connection", connection.Id, SyncChangeKind.Upsert, 1, JsonSerializer.SerializeToElement(connection))], "1", DateTimeOffset.UtcNow, "0.2.0", false, 0));
        var staleAfterRevocation = Connection(selection, string.Empty);
        await store.ApplyAsync(selection, new SyncResponse([
            new SyncItem("access", Guid.CreateVersion7(), SyncChangeKind.AccessRevoked, 2, null),
            new SyncItem("connection", staleAfterRevocation.Id, SyncChangeKind.Upsert, 2, JsonSerializer.SerializeToElement(staleAfterRevocation))
        ], "2", DateTimeOffset.UtcNow, "0.2.0", false, 0));

        var loaded = await store.LoadAsync(selection, true, "revoked");
        Assert.Empty(loaded.Connections);
        Assert.True(loaded.Status.AccessRevoked);
        Assert.True(loaded.Status.IsReadOnly);
    }

    [Fact]
    public async Task LastSelectionOrdersDateTimeOffsetsInMemoryForSqlite()
    {
        using var directory = new TemporaryDirectory();
        var store = new ManagedCacheStore(Path.Combine(directory.Path, "managed-cache.db"));
        await store.InitializeAsync();
        var first = Selection();
        var second = Selection();
        await store.SelectAsync(first);
        await store.SelectAsync(second);

        var selected = await store.LastSelectionAsync();

        Assert.NotNull(selected);
        Assert.Equal(second.OrganizationId, selected.OrganizationId);
        Assert.Equal(second.WorkspaceId, selected.WorkspaceId);
    }

    [Fact]
    public void ManagedOptionsRejectCleartextNonLoopbackControlPlane()
    {
        var options = new ManagedClientOptions(new Uri("http://control.example/"), "client", new Uri("http://127.0.0.1:43876/callback"), "0.2.0");
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public async Task SignedInClientFallsBackToReadOnlyCacheWhenControlPlaneIsUnavailable()
    {
        using var directory = new TemporaryDirectory();
        var store = new ManagedCacheStore(Path.Combine(directory.Path, "managed-cache.db"));
        await store.InitializeAsync();
        var selection = Selection();
        await store.SelectAsync(selection);
        var connection = Connection(selection, string.Empty);
        await store.ApplyAsync(selection, new SyncResponse(
            [new SyncItem("connection", connection.Id, SyncChangeKind.Upsert, 1, JsonSerializer.SerializeToElement(connection))],
            "cursor-1", DateTimeOffset.UtcNow, "0.2.0", false, 1));
        using var httpClient = new HttpClient(new OutageAfterDiscoveryHandler(selection));
        var client = new ManagedWorkspaceClient(httpClient, new StubTokenBroker(), store, ManagedClientOptions.LocalDevelopment("0.2.0"));
        await client.SignInAsync();

        var result = await client.SynchronizeAsync();

        Assert.Single(result.Connections);
        Assert.True(result.Status.IsReadOnly);
        Assert.False(result.Status.IsOnline);
        Assert.False(result.Status.AccessRevoked);
        Assert.Contains("unavailable", result.Status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SynchronizationDrainsEveryServerPageBeforeReportingOnline()
    {
        using var directory = new TemporaryDirectory();
        var store = new ManagedCacheStore(Path.Combine(directory.Path, "managed-cache.db"));
        await store.InitializeAsync();
        var selection = Selection();
        using var handler = new PagedSyncHandler(selection);
        using var httpClient = new HttpClient(handler);
        var client = new ManagedWorkspaceClient(httpClient, new StubTokenBroker(), store, ManagedClientOptions.LocalDevelopment("0.2.0"));

        await client.SignInAsync();
        var result = await client.SynchronizeAsync();

        Assert.True(result.Status.IsOnline);
        Assert.False(result.Status.IsReadOnly);
        Assert.Single(result.Folders);
        Assert.Single(result.Connections);
        Assert.Equal(2, result.Response.Items.Count);
        Assert.False(result.Response.HasMore);
        Assert.Equal(2, handler.SyncRequests);
        Assert.Equal("cursor-2", await store.CursorAsync(selection.OrganizationId, selection.WorkspaceId));
    }

    [Fact]
    public async Task SignInLoadsWorkspacesForTheRestoredOrganizationSelection()
    {
        using var directory = new TemporaryDirectory();
        var store = new ManagedCacheStore(Path.Combine(directory.Path, "managed-cache.db"));
        await store.InitializeAsync();
        var first = Selection();
        var restored = Selection();
        await store.SelectAsync(restored);
        using var handler = new MultiOrganizationDiscoveryHandler(first, restored);
        using var httpClient = new HttpClient(handler);
        var client = new ManagedWorkspaceClient(httpClient, new StubTokenBroker(), store, ManagedClientOptions.LocalDevelopment("0.2.0"));
        await client.LoadCachedAsync();

        var result = await client.SignInAsync();

        Assert.Equal(restored.OrganizationId, handler.RequestedWorkspaceOrganizationId);
        Assert.Equal(restored.OrganizationId, Assert.Single(result.Workspaces).OrganizationId);
        Assert.Equal(restored.OrganizationId, client.Selection?.OrganizationId);
        Assert.Equal(restored.WorkspaceId, client.Selection?.WorkspaceId);
    }

    private static ManagedWorkspaceSelection Selection() => new(Guid.CreateVersion7(), Guid.CreateVersion7(), "Test organization", "Test workspace");

    private static ManagedConnectionDto Connection(ManagedWorkspaceSelection selection, string forbiddenCredential) => new(
        Guid.CreateVersion7(), selection.OrganizationId, selection.WorkspaceId, null, null, ResourceVisibility.Shared,
        "Managed server", "server.example.test", 3389, "DOMAIN\\operator", string.Empty, ConnectionEnvironment.Test,
        forbiddenCredential.Length == 0 ? "metadata only" : "metadata only", ["managed"], EmptyMetadata(), DefaultSettings(), 3, DateTimeOffset.UtcNow);

    private static ConnectionMetadataDto EmptyMetadata() => new("Finance", "App owner", "Service owner", "Important", "EU", "Internal", "Sunday", "Ops", "On call", "asset-1", "cmdb-1", "Windows", "https://monitor.example", "https://docs.example", "https://tickets.example");

    private static ManagedRdpSettingsDto DefaultSettings() => new(
        ManagedConnectionKind.Desktop, true, false, string.Empty, GatewayUsage.Never, string.Empty, 443,
        string.Empty, string.Empty, string.Empty, SessionDisplayMode.Embedded, [], null, null, 32,
        PerformancePreset.Auto, true, ThumbnailMode.Disabled, ShortcutRouting.RemoteInFullScreen, true,
        false, false, false, false, false, false, true, true, true,
        TransferDirection.Bidirectional, TransferDirection.Bidirectional, 0, 3);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nextrdp-client-tests-{Guid.NewGuid():N}"); Directory.CreateDirectory(Path); }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }

    private sealed class StubTokenBroker : IInteractiveTokenBroker
    {
        public Task<DesktopTokenSet> SignInAsync(ManagedClientOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopTokenSet("access", "refresh", DateTimeOffset.UtcNow.AddMinutes(10), "Bearer", "openid offline_access"));

        public Task<DesktopTokenSet> RefreshAsync(ManagedClientOptions options, string refreshToken, CancellationToken cancellationToken = default) =>
            SignInAsync(options, cancellationToken);
    }

    private sealed class OutageAfterDiscoveryHandler(ManagedWorkspaceSelection selection) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/api/v1/organizations")
                return JsonAsync(new[] { new OrganizationDto(selection.OrganizationId, selection.OrganizationName, "test", 1, DateTimeOffset.UtcNow) });
            if (path == $"/api/v1/organizations/{selection.OrganizationId:D}/workspaces")
                return JsonAsync(new[] { new WorkspaceDto(selection.WorkspaceId, selection.OrganizationId, selection.WorkspaceName, "test", string.Empty, 1, DateTimeOffset.UtcNow) });
            throw new HttpRequestException("simulated control-plane outage");
        }

        private static Task<HttpResponseMessage> JsonAsync<T>(T value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        });
    }

    private sealed class MultiOrganizationDiscoveryHandler(ManagedWorkspaceSelection first, ManagedWorkspaceSelection restored) : HttpMessageHandler
    {
        public Guid? RequestedWorkspaceOrganizationId { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/api/v1/organizations")
                return JsonAsync(new[] { Organization(first), Organization(restored) });
            if (path == $"/api/v1/organizations/{first.OrganizationId:D}/workspaces")
            {
                RequestedWorkspaceOrganizationId = first.OrganizationId;
                return JsonAsync(new[] { Workspace(first) });
            }
            if (path == $"/api/v1/organizations/{restored.OrganizationId:D}/workspaces")
            {
                RequestedWorkspaceOrganizationId = restored.OrganizationId;
                return JsonAsync(new[] { Workspace(restored) });
            }
            throw new InvalidOperationException($"Unexpected discovery request: {path}");
        }

        private static OrganizationDto Organization(ManagedWorkspaceSelection selection) =>
            new(selection.OrganizationId, selection.OrganizationName, selection.OrganizationId.ToString("N"), 1, DateTimeOffset.UtcNow);

        private static WorkspaceDto Workspace(ManagedWorkspaceSelection selection) =>
            new(selection.WorkspaceId, selection.OrganizationId, selection.WorkspaceName, selection.WorkspaceId.ToString("N"), string.Empty, 1, DateTimeOffset.UtcNow);

        private static Task<HttpResponseMessage> JsonAsync<T>(T value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        });
    }

    private sealed class PagedSyncHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };
        private readonly ManagedWorkspaceSelection selection;
        private readonly FolderDto folder;
        private readonly ManagedConnectionDto connection;

        public PagedSyncHandler(ManagedWorkspaceSelection selection)
        {
            this.selection = selection;
            folder = new FolderDto(Guid.CreateVersion7(), selection.OrganizationId, selection.WorkspaceId, null, "Production", 0, 1, DateTimeOffset.UtcNow);
            connection = Connection(selection, string.Empty) with { FolderId = folder.Id };
        }

        public int SyncRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/api/v1/organizations")
                return Json(new[] { new OrganizationDto(selection.OrganizationId, selection.OrganizationName, "test", 1, DateTimeOffset.UtcNow) });
            if (path == $"/api/v1/organizations/{selection.OrganizationId:D}/workspaces")
                return Json(new[] { new WorkspaceDto(selection.WorkspaceId, selection.OrganizationId, selection.WorkspaceName, "test", string.Empty, 1, DateTimeOffset.UtcNow) });
            if (path.EndsWith("/sync", StringComparison.Ordinal))
            {
                SyncRequests++;
                var requestBody = await request.Content!.ReadFromJsonAsync<SyncRequest>(JsonOptions, cancellationToken);
                return string.IsNullOrWhiteSpace(requestBody?.Cursor)
                    ? Json(new SyncResponse([new SyncItem("folder", folder.Id, SyncChangeKind.Upsert, 1, JsonSerializer.SerializeToElement(folder, JsonOptions))],
                        "cursor-1", DateTimeOffset.UtcNow, "0.2.0", false, 1, true))
                    : Json(new SyncResponse([new SyncItem("connection", connection.Id, SyncChangeKind.Upsert, 1, JsonSerializer.SerializeToElement(connection, JsonOptions))],
                        "cursor-2", DateTimeOffset.UtcNow, "0.2.0", false, 1, false));
            }
            if (path.EndsWith("/configuration/effective", StringComparison.Ordinal))
                return Json(new EffectivePolicy(selection.OrganizationId, selection.WorkspaceId, folder.Id, connection.Id, 1, new Dictionary<string, EffectivePolicyField>(), []));
            if (path.EndsWith("/dynamic-groups", StringComparison.Ordinal)) return Json(Array.Empty<DynamicGroupDto>());
            if (path.EndsWith("/connection-templates", StringComparison.Ordinal)) return Json(Array.Empty<ConnectionTemplateDto>());
            if (path.EndsWith("/me/workspace-state", StringComparison.Ordinal))
                return Json(new UserWorkspaceStateDto(selection.OrganizationId, selection.WorkspaceId, Guid.CreateVersion7(), [],
                    new LayoutPreferencesDto(320, "name", "system", true), [], [], 0, DateTimeOffset.UtcNow));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json")
        };
    }
}
