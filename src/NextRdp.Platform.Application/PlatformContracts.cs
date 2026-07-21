using System.Text.Json;
using NextRdp.Core;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Application;

public sealed record CreateOrganizationRequest(string Name, string Slug);
public sealed record OrganizationDto(Guid Id, string Name, string Slug, long Revision, DateTimeOffset UpdatedAtUtc);

public sealed record CreateWorkspaceRequest(string Name, string Slug, string Description);
public sealed record WorkspaceDto(Guid Id, Guid OrganizationId, string Name, string Slug, string Description, long Revision, DateTimeOffset UpdatedAtUtc);

public sealed record PlatformUserDto(Guid Id, string DisplayName, string EmailHint, MembershipStatus Status, DateTimeOffset LastAuthenticatedAtUtc, long SessionVersion);
public sealed record UpsertMembershipRequest(Guid UserId, Guid? WorkspaceId, WorkspaceRole Role, MembershipStatus Status, string SourceGroup, long? ExpectedRevision);
public sealed record MembershipDto(Guid Id, Guid OrganizationId, Guid? WorkspaceId, Guid UserId, WorkspaceRole Role, MembershipStatus Status, string SourceGroup, long Revision, DateTimeOffset UpdatedAtUtc);
public sealed record SaveGroupRoleMappingRequest(Guid? WorkspaceId, string GroupClaim, WorkspaceRole Role, bool Enabled, long? ExpectedRevision);
public sealed record GroupRoleMappingDto(Guid Id, Guid OrganizationId, Guid? WorkspaceId, string GroupClaim, WorkspaceRole Role, bool Enabled, long Revision, DateTimeOffset UpdatedAtUtc);

public sealed record UpsertFolderRequest(Guid? ParentId, string Name, int SortOrder, long? ExpectedRevision);
public sealed record FolderDto(Guid Id, Guid OrganizationId, Guid WorkspaceId, Guid? ParentId, string Name, int SortOrder, long Revision, DateTimeOffset UpdatedAtUtc);

public sealed record ConnectionMetadataDto(
    string BusinessUnit,
    string ApplicationOwner,
    string ServiceOwner,
    string Criticality,
    string Geography,
    string DataClassification,
    string MaintenanceWindow,
    string SupportGroup,
    string EscalationContact,
    string AssetId,
    string CmdbId,
    string OperatingSystem,
    string MonitoringUrl,
    string DocumentationUrl,
    string TicketingUrl);

public sealed record ManagedRdpSettingsDto(
    ManagedConnectionKind Kind,
    bool RequireNetworkLevelAuthentication,
    bool AdministrativeSession,
    string BrokerLoadBalanceInfo,
    GatewayUsage GatewayUsage,
    string GatewayHost,
    int GatewayPort,
    string RemoteAppProgram,
    string RemoteAppWorkingDirectory,
    string RemoteAppArguments,
    SessionDisplayMode DisplayMode,
    IReadOnlyList<uint> SelectedMonitorIds,
    int? DesktopWidth,
    int? DesktopHeight,
    int ColorDepth,
    PerformancePreset PerformancePreset,
    bool ShowConnectionQuality,
    ThumbnailMode ThumbnailMode,
    ShortcutRouting ShortcutRouting,
    bool AllowSecureAttentionSequence,
    bool RedirectMicrophone,
    bool RedirectCamera,
    bool RedirectComPorts,
    bool RedirectLptPorts,
    bool RedirectUsbDevices,
    bool RedirectSmartCards,
    bool RedirectPrinters,
    bool RedirectDrives,
    bool RedirectAudio,
    TransferDirection ClipboardDirection,
    TransferDirection FileTransferDirection,
    int SessionTimeoutMinutes,
    int ReconnectAttempts);

public sealed record UpsertManagedConnectionRequest(
    Guid? FolderId,
    ResourceVisibility Visibility,
    string Name,
    string Host,
    int Port,
    string UsernameHint,
    ConnectionEnvironment Environment,
    string Notes,
    IReadOnlyList<string> Tags,
    ConnectionMetadataDto Metadata,
    ManagedRdpSettingsDto Settings,
    long? ExpectedRevision);

public sealed record ManagedConnectionDto(
    Guid Id,
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid? FolderId,
    Guid? OwnerUserId,
    ResourceVisibility Visibility,
    string Name,
    string Host,
    int Port,
    string UsernameHint,
    string ProvisioningKey,
    ConnectionEnvironment Environment,
    string Notes,
    IReadOnlyList<string> Tags,
    ConnectionMetadataDto Metadata,
    ManagedRdpSettingsDto Settings,
    long Revision,
    DateTimeOffset UpdatedAtUtc);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor, int PageSize);

public sealed record SaveConfigurationDraftRequest(
    ConfigurationScopeType ScopeType,
    Guid ScopeId,
    int BaseVersion,
    JsonElement Values,
    IReadOnlyList<string> LockedFields,
    string Summary,
    long? ExpectedRevision);

public sealed record ConfigurationDraftDto(
    Guid Id,
    Guid OrganizationId,
    Guid? WorkspaceId,
    ConfigurationScopeType ScopeType,
    Guid ScopeId,
    int BaseVersion,
    JsonElement Values,
    IReadOnlyList<string> LockedFields,
    string Summary,
    long Revision,
    DateTimeOffset UpdatedAtUtc);

public sealed record PublishedConfigurationDto(
    Guid Id,
    Guid OrganizationId,
    Guid? WorkspaceId,
    ConfigurationScopeType ScopeType,
    Guid ScopeId,
    int Version,
    Guid? RolledBackFromId,
    JsonElement Values,
    IReadOnlyList<string> LockedFields,
    string Summary,
    DateTimeOffset PublishedAtUtc);

public sealed record SyncRequest(string? Cursor, string ClientVersion, IReadOnlyDictionary<Guid, long> KnownRevisions);
public sealed record SyncItem(string ResourceType, Guid ResourceId, SyncChangeKind Kind, long Revision, JsonElement? Payload);
public sealed record SyncResponse(
    IReadOnlyList<SyncItem> Items,
    string NextCursor,
    DateTimeOffset ServerTimeUtc,
    string MinimumClientVersion,
    bool ClientUpgradeRequired,
    long EffectivePolicyRevision,
    bool HasMore = false);

public sealed record AuditEventDto(
    Guid Id,
    Guid OrganizationId,
    Guid? WorkspaceId,
    string ActorAlias,
    string Action,
    string ResourceType,
    Guid? ResourceId,
    AuditOutcome Outcome,
    JsonElement RedactedDetail,
    string CorrelationId,
    DateTimeOffset OccurredAtUtc);

public sealed record BulkConnectionItem(string ExternalKey, UpsertManagedConnectionRequest Connection);
public sealed record BulkProvisionRequest(bool Atomic, IReadOnlyList<BulkConnectionItem> Items);
public sealed record BulkItemResult(string ExternalKey, Guid? ResourceId, bool Succeeded, string Code, string Message);
public sealed record BulkProvisionResult(IReadOnlyList<BulkItemResult> Items, bool Replayed);

public sealed record ImportPreviewRequest(string Format, string Content);
public sealed record ImportCandidateDto(string ExternalKey, UpsertManagedConnectionRequest? Connection, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);
public sealed record ManagedImportPreview(Guid PreviewId, IReadOnlyList<ImportCandidateDto> Candidates, int SecretFieldsDiscarded, DateTimeOffset ExpiresAtUtc);
public sealed record CommitImportRequest(Guid PreviewId, IReadOnlyList<string> SelectedExternalKeys);

public sealed record CreateServiceAccountRequest(string Name, Guid? WorkspaceId, IReadOnlyList<string> Scopes, DateTimeOffset? ExpiresAtUtc);
public sealed record CreatedServiceAccount(Guid Id, string Name, string ClientId, string ClientSecret, DateTimeOffset? ExpiresAtUtc);
public sealed record RotatedServiceAccountSecret(Guid Id, string ClientId, string ClientSecret, DateTimeOffset RotatedAtUtc);
public sealed record ServiceAccountDto(Guid Id, string Name, string ClientId, Guid? WorkspaceId, IReadOnlyList<string> Scopes, DateTimeOffset? ExpiresAtUtc, DateTimeOffset? RevokedAtUtc);

public sealed record ExportEnvelope(int SchemaVersion, DateTimeOffset ExportedAtUtc, Guid OrganizationId, Guid WorkspaceId, IReadOnlyList<FolderDto> Folders, IReadOnlyList<ManagedConnectionDto> Connections);

public sealed record BackupRequest(string Passphrase, bool IncludeAudit);
public sealed record BackupArtifact(string FileName, byte[] Content, string Sha256);
public sealed record RestoreResult(int Organizations, int Workspaces, int Folders, int Connections, int Configurations, bool IntegrityVerified);

public sealed record DynamicGroupRuleDto(string Field, string Operator, string Value);
public sealed record SaveDynamicGroupRequest(string Name, IReadOnlyList<DynamicGroupRuleDto> Rules, long? ExpectedRevision);
public sealed record DynamicGroupDto(Guid Id, Guid OrganizationId, Guid WorkspaceId, string Name, IReadOnlyList<DynamicGroupRuleDto> Rules, IReadOnlyList<Guid> MatchingConnectionIds, long Revision, DateTimeOffset UpdatedAtUtc);

public sealed record TemplateProfileDefaults(
    ConnectionEnvironment Environment,
    string Notes,
    IReadOnlyList<string> Tags,
    ConnectionMetadataDto Metadata,
    ManagedRdpSettingsDto Settings);
public sealed record SaveConnectionTemplateRequest(string Name, string Description, TemplateProfileDefaults Defaults, long? ExpectedRevision);
public sealed record ConnectionTemplateDto(Guid Id, Guid OrganizationId, Guid WorkspaceId, string Name, string Description, TemplateProfileDefaults Defaults, long Revision, DateTimeOffset UpdatedAtUtc);
public sealed record CreateConnectionFromTemplateRequest(Guid TemplateId, Guid? FolderId, ResourceVisibility Visibility, string Name, string Host, int Port, string UsernameHint);
public sealed record CopyManagedConnectionRequest(string Name, Guid? FolderId, ResourceVisibility Visibility);

public sealed record LayoutPreferencesDto(int NavigationWidth, string InventorySort, string Theme, bool ReopenLastWorkspace);
public sealed record SavedWorkspaceDto(Guid Id, string Name, IReadOnlyList<Guid> ConnectionIds, SessionDisplayMode DisplayMode);
public sealed record TabGroupDto(Guid Id, string Name, string Color, IReadOnlyList<Guid> ConnectionIds);
public sealed record SaveUserWorkspaceStateRequest(IReadOnlyList<Guid> PinnedFolderIds, LayoutPreferencesDto Layout, IReadOnlyList<SavedWorkspaceDto> SavedWorkspaces, IReadOnlyList<TabGroupDto> TabGroups, long? ExpectedRevision);
public sealed record UserWorkspaceStateDto(Guid OrganizationId, Guid WorkspaceId, Guid UserId, IReadOnlyList<Guid> PinnedFolderIds, LayoutPreferencesDto Layout, IReadOnlyList<SavedWorkspaceDto> SavedWorkspaces, IReadOnlyList<TabGroupDto> TabGroups, long Revision, DateTimeOffset UpdatedAtUtc);

public sealed record ConnectionMetadataPatchDto(
    string? BusinessUnit = null,
    string? ApplicationOwner = null,
    string? ServiceOwner = null,
    string? Criticality = null,
    string? Geography = null,
    string? DataClassification = null,
    string? MaintenanceWindow = null,
    string? SupportGroup = null,
    string? EscalationContact = null,
    string? AssetId = null,
    string? CmdbId = null,
    string? OperatingSystem = null,
    string? MonitoringUrl = null,
    string? DocumentationUrl = null,
    string? TicketingUrl = null);

public sealed record BulkConnectionEditRequest(IReadOnlyList<Guid> ConnectionIds, ConnectionEnvironment? Environment, Guid? FolderId, bool ClearFolder, IReadOnlyList<string> AddTags, IReadOnlyList<string> RemoveTags, string? ApplicationOwner, string? Criticality, ConnectionMetadataPatchDto? Metadata = null);
public sealed record BulkConnectPreviewRequest(IReadOnlyList<Guid> ConnectionIds);
public sealed record BulkConnectPreviewResult(IReadOnlyList<Guid> AllowedConnectionIds, IReadOnlyList<Guid> DeniedConnectionIds, int MaximumConnections, IReadOnlyList<string> Warnings);

public sealed record GatewayDiagnosticRequest(string Host, int Port, string? ExpectedServerName);
public sealed record GatewayDiagnosticResult(bool DnsSucceeded, int AddressCount, bool TcpSucceeded, bool TlsSucceeded, string Outcome, long DurationMilliseconds, DateTimeOffset TestedAtUtc);
