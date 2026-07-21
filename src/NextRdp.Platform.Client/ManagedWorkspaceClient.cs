using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NextRdp.Platform.Application;

namespace NextRdp.Platform.Client;

public sealed class ManagedWorkspaceClient(
    HttpClient httpClient,
    IInteractiveTokenBroker tokenBroker,
    ManagedCacheStore cache,
    ManagedClientOptions options) : IManagedWorkspaceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private DesktopTokenSet? tokens;
    public bool IsSignedIn => tokens is not null;
    public ManagedWorkspaceSelection? Selection { get; private set; }

    public async Task<(IReadOnlyList<OrganizationDto> Organizations, IReadOnlyList<WorkspaceDto> Workspaces)> SignInAsync(CancellationToken cancellationToken = default)
    {
        options.Validate();
        tokens = await tokenBroker.SignInAsync(options, cancellationToken);
        var organizations = await SendAsync<IReadOnlyList<OrganizationDto>>(HttpMethod.Get, "api/v1/organizations", null, cancellationToken);
        var organization = organizations.FirstOrDefault(item => item.Id == Selection?.OrganizationId) ?? (organizations.Count == 0 ? null : organizations[0]);
        var workspaces = organization is null ? [] : await ListWorkspacesAsync(organization.Id, cancellationToken);
        var workspace = workspaces.FirstOrDefault(item => item.Id == Selection?.WorkspaceId);
        if (organization is not null && workspace is not null)
            await SelectWorkspaceAsync(organization, workspace, cancellationToken);
        else if (organizations.Count == 1 && workspaces.Count == 1)
            await SelectWorkspaceAsync(organization!, workspaces[0], cancellationToken);
        else
            Selection = null;
        return (organizations, workspaces);
    }

    public Task<IReadOnlyList<WorkspaceDto>> ListWorkspacesAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<WorkspaceDto>>(HttpMethod.Get, $"api/v1/organizations/{organizationId:D}/workspaces", null, cancellationToken);

    public async Task SelectWorkspaceAsync(OrganizationDto organization, WorkspaceDto workspace, CancellationToken cancellationToken = default)
    {
        if (workspace.OrganizationId != organization.Id) throw new ArgumentException("The workspace does not belong to the selected organization.");
        Selection = new ManagedWorkspaceSelection(organization.Id, workspace.Id, organization.Name, workspace.Name);
        await cache.SelectAsync(Selection, cancellationToken);
    }

    public async Task<ManagedSyncResult> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        var selection = Selection ?? (tokens is null ? await RestoreSelectionAsync(cancellationToken) : null)
            ?? throw new InvalidOperationException("Select a managed workspace before synchronizing.");
        if (tokens is null) return await CachedResultAsync(selection, "Sign in to refresh this read-only managed cache.", cancellationToken);
        try
        {
            var cursor = await cache.CursorAsync(selection.OrganizationId, selection.WorkspaceId, cancellationToken);
            var revisions = await cache.KnownRevisionsAsync(selection.OrganizationId, selection.WorkspaceId, cancellationToken);
            var received = new List<SyncItem>();
            SyncResponse? response = null;
            for (var page = 0; page < 100; page++)
            {
                var pageResponse = await SendAsync<SyncResponse>(HttpMethod.Post,
                    $"api/v1/organizations/{selection.OrganizationId:D}/workspaces/{selection.WorkspaceId:D}/sync",
                    new SyncRequest(string.IsNullOrWhiteSpace(cursor) ? null : cursor, options.ClientVersion, revisions), cancellationToken);
                if (pageResponse.HasMore && string.Equals(pageResponse.NextCursor, cursor, StringComparison.Ordinal))
                    throw new InvalidDataException("The synchronization feed reported more pages without advancing its cursor.");
                await cache.ApplyAsync(selection, pageResponse, cancellationToken);
                received.AddRange(pageResponse.Items);
                response = pageResponse;
                cursor = pageResponse.NextCursor;
                if (!pageResponse.HasMore) break;
            }
            if (response is null || response.HasMore)
                throw new InvalidDataException("The synchronization feed exceeded the 100-page safety limit.");
            response = response with { Items = received, HasMore = false };
            var loaded = await cache.LoadAsync(selection, true, response.ClientUpgradeRequired ? $"Client {response.MinimumClientVersion} or newer is required; managed launch is disabled." : "Managed inventory is synchronized.", cancellationToken);
            foreach (var connection in loaded.Connections)
            {
                var query = $"api/v1/organizations/{selection.OrganizationId:D}/workspaces/{selection.WorkspaceId:D}/configuration/effective?connectionId={connection.Id:D}" +
                    (connection.FolderId is Guid folderId ? $"&folderId={folderId:D}" : string.Empty);
                var policy = await SendAsync<NextRdp.Platform.Domain.EffectivePolicy>(HttpMethod.Get, query, null, cancellationToken);
                await cache.SaveEffectivePolicyAsync(selection, policy, cancellationToken);
            }
            var productivity = await LoadProductivityAsync(cancellationToken);
            return new ManagedSyncResult(response, loaded.Status, loaded.Connections, loaded.Folders)
            {
                EffectivePolicies = await cache.LoadEffectivePoliciesAsync(selection, cancellationToken),
                Productivity = productivity
            };
        }
        catch (ManagedApiException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            await cache.RevokeAsync(selection, cancellationToken);
            tokens = null;
            var loaded = await cache.LoadAsync(selection, false, "Managed access was revoked or expired. Sign in again; cached managed resources were removed.", cancellationToken);
            return new ManagedSyncResult(EmptySync(loaded.Status), loaded.Status, loaded.Connections, loaded.Folders);
        }
        catch (HttpRequestException)
        {
            return await CachedResultAsync(selection, "The control plane is unavailable. Managed metadata is read-only and managed launch is blocked.", cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await CachedResultAsync(selection, "The control-plane request timed out. Managed metadata is read-only and managed launch is blocked.", cancellationToken);
        }
    }

    public async Task<ManagedSyncResult> LoadCachedAsync(CancellationToken cancellationToken = default)
    {
        var selection = Selection ?? await RestoreSelectionAsync(cancellationToken) ?? throw new InvalidOperationException("No synchronized managed workspace is cached on this device.");
        return await CachedResultAsync(selection, "Showing cached managed metadata. Sign in and synchronize before launching a managed connection.", cancellationToken);
    }

    public async Task<ManagedProductivitySnapshot> LoadProductivityAsync(CancellationToken cancellationToken = default)
    {
        var selection = Selection ?? throw new InvalidOperationException("Select a managed workspace first.");
        var root = $"api/v1/organizations/{selection.OrganizationId:D}/workspaces/{selection.WorkspaceId:D}";
        var groups = await SendAsync<IReadOnlyList<DynamicGroupDto>>(HttpMethod.Get, $"{root}/dynamic-groups", null, cancellationToken);
        var templates = await SendAsync<IReadOnlyList<ConnectionTemplateDto>>(HttpMethod.Get, $"{root}/connection-templates", null, cancellationToken);
        var state = await SendAsync<UserWorkspaceStateDto>(HttpMethod.Get, $"{root}/me/workspace-state", null, cancellationToken);
        var snapshot = new ManagedProductivitySnapshot(groups, templates, state);
        await cache.SaveProductivityAsync(selection, snapshot, cancellationToken);
        return snapshot;
    }

    public Task<UserWorkspaceStateDto> SaveWorkspaceStateAsync(SaveUserWorkspaceStateRequest request, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        var selection = Selection ?? throw new InvalidOperationException("Select a managed workspace first.");
        var headers = expectedRevision is null ? null : new Dictionary<string, string> { ["If-Match"] = $"\"{expectedRevision.Value}\"" };
        return SendAsync<UserWorkspaceStateDto>(HttpMethod.Put, $"api/v1/organizations/{selection.OrganizationId:D}/workspaces/{selection.WorkspaceId:D}/me/workspace-state", request, cancellationToken, headers);
    }

    public Task<BulkConnectPreviewResult> PreviewBulkConnectAsync(IReadOnlyList<Guid> connectionIds, CancellationToken cancellationToken = default)
    {
        var selection = Selection ?? throw new InvalidOperationException("Select a managed workspace first.");
        return SendAsync<BulkConnectPreviewResult>(HttpMethod.Post, $"api/v1/organizations/{selection.OrganizationId:D}/workspaces/{selection.WorkspaceId:D}/bulk/connect-preview", new BulkConnectPreviewRequest(connectionIds), cancellationToken);
    }

    public Task<ManagedConnectionDto> CreateFromTemplateAsync(CreateConnectionFromTemplateRequest request, CancellationToken cancellationToken = default)
    {
        var selection = Selection ?? throw new InvalidOperationException("Select a managed workspace first.");
        return SendAsync<ManagedConnectionDto>(HttpMethod.Post, $"api/v1/organizations/{selection.OrganizationId:D}/workspaces/{selection.WorkspaceId:D}/connections/from-template", request, cancellationToken);
    }

    public Task<ManagedConnectionDto> CopyConnectionAsync(Guid connectionId, CopyManagedConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var selection = Selection ?? throw new InvalidOperationException("Select a managed workspace first.");
        return SendAsync<ManagedConnectionDto>(HttpMethod.Post, $"api/v1/organizations/{selection.OrganizationId:D}/workspaces/{selection.WorkspaceId:D}/connections/{connectionId:D}/copy", request, cancellationToken);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (tokens is not null)
                await SendAsync<object>(HttpMethod.Post, "auth/global-logout", null, cancellationToken);
        }
        catch (ManagedApiException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // The server already considers this product session ended.
        }
        catch (HttpRequestException)
        {
            // Local sign-out must still complete while the control plane is unavailable.
        }
        finally
        {
            tokens = null;
        }
    }

    private async Task<ManagedWorkspaceSelection?> RestoreSelectionAsync(CancellationToken cancellationToken)
    {
        Selection = await cache.LastSelectionAsync(cancellationToken);
        return Selection;
    }

    private async Task<ManagedSyncResult> CachedResultAsync(ManagedWorkspaceSelection selection, string message, CancellationToken cancellationToken)
    {
        var loaded = await cache.LoadAsync(selection, false, message, cancellationToken);
        return new ManagedSyncResult(EmptySync(loaded.Status), loaded.Status, loaded.Connections, loaded.Folders)
        {
            EffectivePolicies = await cache.LoadEffectivePoliciesAsync(selection, cancellationToken),
            Productivity = await cache.LoadProductivityAsync(selection, cancellationToken)
        };
    }

    private static SyncResponse EmptySync(ManagedCacheStatus status) => new([], string.Empty, DateTimeOffset.UtcNow, status.MinimumClientVersion, status.ClientUpgradeRequired, status.EffectivePolicyRevision);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? headers = null)
    {
        if (tokens is null) throw new InvalidOperationException("Sign in before calling the managed control plane.");
        if (tokens.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1) && !string.IsNullOrWhiteSpace(tokens.RefreshToken))
            tokens = await tokenBroker.RefreshAsync(options, tokens.RefreshToken, cancellationToken);
        using var request = new HttpRequestMessage(method, new Uri(options.ControlPlane, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (headers is not null)
            foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await ReadProblemAsync(response, cancellationToken);
            throw new ManagedApiException(response.StatusCode, problem.Code, problem.Message);
        }
        if (response.StatusCode == HttpStatusCode.NoContent) return default!;
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken) ?? throw new InvalidDataException("The control-plane response was empty or invalid.");
    }

    private static async Task<(string Code, string Message)> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ApiProblem>(JsonOptions, cancellationToken);
            return (problem?.Code ?? "request_failed", problem?.Detail ?? problem?.Title ?? $"Request failed ({(int)response.StatusCode}).");
        }
        catch (JsonException) { return ("request_failed", $"Request failed ({(int)response.StatusCode})."); }
    }

    private sealed record ApiProblem(string? Code, string? Title, string? Detail);
}

public sealed class ManagedApiException(HttpStatusCode statusCode, string code, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
