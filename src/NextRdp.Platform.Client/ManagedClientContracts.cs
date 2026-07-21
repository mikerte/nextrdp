using NextRdp.Platform.Application;

namespace NextRdp.Platform.Client;

public sealed record ManagedClientOptions(Uri ControlPlane, string ClientId, Uri RedirectUri, string ClientVersion)
{
    public static ManagedClientOptions LocalDevelopment(string clientVersion) => new(
        new Uri("http://127.0.0.1:5080/"), "nextrdp-desktop", new Uri("http://127.0.0.1:43876/callback"), clientVersion);

    public void Validate()
    {
        if (!ControlPlane.IsAbsoluteUri || ControlPlane.Scheme is not ("https" or "http")) throw new ArgumentException("Control-plane URL must be absolute HTTP or HTTPS.");
        if (ControlPlane.Scheme == "http" && !ControlPlane.IsLoopback) throw new ArgumentException("Unencrypted control-plane URLs are allowed only on loopback.");
        if (string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 200) throw new ArgumentException("OIDC client ID is invalid.");
        if (!RedirectUri.IsLoopback || RedirectUri.Scheme != Uri.UriSchemeHttp) throw new ArgumentException("Desktop OIDC redirect must be an HTTP loopback URI.");
        if (!SemanticVersion.TryParse(ClientVersion, out _)) throw new ArgumentException("Client version must use semantic-version format.");
    }
}

public sealed record DesktopTokenSet(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAtUtc, string TokenType, string Scope);

public interface IInteractiveTokenBroker
{
    Task<DesktopTokenSet> SignInAsync(ManagedClientOptions options, CancellationToken cancellationToken = default);
    Task<DesktopTokenSet> RefreshAsync(ManagedClientOptions options, string refreshToken, CancellationToken cancellationToken = default);
}

public sealed record ManagedWorkspaceSelection(Guid OrganizationId, Guid WorkspaceId, string OrganizationName, string WorkspaceName);
public sealed record ManagedCacheStatus(Guid OrganizationId, Guid WorkspaceId, bool IsOnline, bool IsReadOnly, bool AccessRevoked, DateTimeOffset? LastSyncAtUtc, string MinimumClientVersion, bool ClientUpgradeRequired, long EffectivePolicyRevision, string Message);
public sealed record ManagedSyncResult(SyncResponse Response, ManagedCacheStatus Status, IReadOnlyList<ManagedConnectionDto> Connections, IReadOnlyList<FolderDto> Folders)
{
    public IReadOnlyDictionary<Guid, NextRdp.Platform.Domain.EffectivePolicy> EffectivePolicies { get; init; } = new Dictionary<Guid, NextRdp.Platform.Domain.EffectivePolicy>();
    public ManagedProductivitySnapshot? Productivity { get; init; }
}
public sealed record ManagedProductivitySnapshot(IReadOnlyList<NextRdp.Platform.Application.DynamicGroupDto> DynamicGroups, IReadOnlyList<NextRdp.Platform.Application.ConnectionTemplateDto> Templates, NextRdp.Platform.Application.UserWorkspaceStateDto WorkspaceState);

public interface IManagedWorkspaceClient
{
    bool IsSignedIn { get; }
    ManagedWorkspaceSelection? Selection { get; }
    Task<(IReadOnlyList<OrganizationDto> Organizations, IReadOnlyList<WorkspaceDto> Workspaces)> SignInAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkspaceDto>> ListWorkspacesAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task SelectWorkspaceAsync(OrganizationDto organization, WorkspaceDto workspace, CancellationToken cancellationToken = default);
    Task<ManagedSyncResult> SynchronizeAsync(CancellationToken cancellationToken = default);
    Task<ManagedSyncResult> LoadCachedAsync(CancellationToken cancellationToken = default);
    Task<ManagedProductivitySnapshot> LoadProductivityAsync(CancellationToken cancellationToken = default);
    Task<UserWorkspaceStateDto> SaveWorkspaceStateAsync(SaveUserWorkspaceStateRequest request, long? expectedRevision, CancellationToken cancellationToken = default);
    Task<BulkConnectPreviewResult> PreviewBulkConnectAsync(IReadOnlyList<Guid> connectionIds, CancellationToken cancellationToken = default);
    Task<ManagedConnectionDto> CreateFromTemplateAsync(CreateConnectionFromTemplateRequest request, CancellationToken cancellationToken = default);
    Task<ManagedConnectionDto> CopyConnectionAsync(Guid connectionId, CopyManagedConnectionRequest request, CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
}
