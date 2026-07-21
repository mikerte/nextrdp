namespace NextRdp.Platform.Domain;

public enum ResourceVisibility
{
    Personal,
    Shared
}

public enum WorkspaceRole
{
    PlatformAdministrator,
    OrganizationAdministrator,
    WorkspaceAdministrator,
    Operator,
    Auditor
}

public enum MembershipStatus
{
    Active,
    Suspended,
    Revoked
}

public enum ConfigurationScopeType
{
    Organization,
    Workspace,
    Folder,
    Connection
}

public enum ConfigurationLifecycle
{
    Draft,
    Published,
    Superseded
}

public enum ManagedConnectionKind
{
    Desktop,
    RemoteApp
}

public enum GatewayUsage
{
    Never,
    Always,
    Detect
}

public enum SessionDisplayMode
{
    Embedded,
    SeparateWindow,
    FullScreen,
    AllMonitors,
    SelectedMonitors
}

public enum PerformancePreset
{
    Auto,
    Lan,
    Broadband,
    LowBandwidth
}

public enum ShortcutRouting
{
    Local,
    RemoteWhenFocused,
    RemoteInFullScreen
}

public enum TransferDirection
{
    Disabled,
    ClientToRemote,
    RemoteToClient,
    Bidirectional
}

public enum ThumbnailMode
{
    Disabled,
    Enabled,
    PrivacyLocked
}

public enum SyncChangeKind
{
    Upsert,
    Delete,
    AccessRevoked
}

public enum AuditOutcome
{
    Succeeded,
    Denied,
    Failed
}
