using System.Net;

namespace NextRdp.Core;

public enum ConnectionEnvironment
{
    Production,
    Test,
    Development,
    DisasterRecovery,
    Lab
}

public enum SessionState
{
    Created,
    Validating,
    Connecting,
    Connected,
    Reconnecting,
    Disconnected,
    Failed
}

public enum AuditEventType
{
    ConnectionAttempted,
    ConnectionSucceeded,
    ConnectionFailed,
    SessionDisconnected,
    ImportPreviewed,
    ImportCompleted,
    DiagnosticExecuted,
    InventoryExported,
    SupportPackageCreated,
    RecoveryCompleted
}

public enum RdpConnectionKind { Desktop, RemoteApp }
public enum RdpGatewayUsage { Never, Always, Detect }
public enum RdpDisplayMode { Embedded, SeparateWindow, FullScreen, AllMonitors, SelectedMonitors }
public enum RdpPerformancePreset { Auto, Lan, Broadband, LowBandwidth }
public enum RdpShortcutRouting { Local, RemoteWhenFocused, RemoteInFullScreen }
public enum RdpTransferDirection { Disabled, ClientToRemote, RemoteToClient, Bidirectional }
public enum RdpThumbnailMode { Disabled, Enabled, PrivacyLocked }

public sealed record RdpSettings
{
    public RdpConnectionKind Kind { get; init; }
    public bool RequireNetworkLevelAuthentication { get; init; } = true;
    public bool AdministrativeSession { get; init; }
    public string BrokerLoadBalanceInfo { get; init; } = string.Empty;
    public RdpGatewayUsage GatewayUsage { get; init; }
    public string GatewayHost { get; init; } = string.Empty;
    public int GatewayPort { get; init; } = 443;
    public string RemoteAppProgram { get; init; } = string.Empty;
    public string RemoteAppWorkingDirectory { get; init; } = string.Empty;
    public string RemoteAppArguments { get; init; } = string.Empty;
    public RdpDisplayMode DisplayMode { get; init; } = RdpDisplayMode.Embedded;
    public IReadOnlyList<uint> SelectedMonitorIds { get; init; } = [];
    public int? DesktopWidth { get; init; }
    public int? DesktopHeight { get; init; }
    public bool SmartSizing { get; init; } = true;
    public bool RedirectClipboard { get; init; } = true;
    public bool RedirectDrives { get; init; }
    public bool RedirectPrinters { get; init; }
    public bool RedirectAudio { get; init; } = true;
    public bool RedirectMicrophone { get; init; }
    public bool RedirectCamera { get; init; }
    public bool RedirectComPorts { get; init; }
    public bool RedirectLptPorts { get; init; }
    public bool RedirectUsbDevices { get; init; }
    public bool RedirectSmartCards { get; init; }
    public RdpTransferDirection ClipboardDirection { get; init; } = RdpTransferDirection.Bidirectional;
    public RdpTransferDirection FileTransferDirection { get; init; } = RdpTransferDirection.Disabled;
    public int ColorDepth { get; init; } = 32;
    public RdpPerformancePreset PerformancePreset { get; init; }
    public bool ShowConnectionQuality { get; init; } = true;
    public RdpThumbnailMode ThumbnailMode { get; init; } = RdpThumbnailMode.Disabled;
    public RdpShortcutRouting ShortcutRouting { get; init; } = RdpShortcutRouting.RemoteInFullScreen;
    public bool AllowSecureAttentionSequence { get; init; } = true;
    public int SessionTimeoutMinutes { get; init; }
    public int ReconnectAttempts { get; init; } = 5;
    public bool ShowProductionWatermark { get; init; } = true;
}

public sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? FolderId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 3389;
    public string? UsernameHint { get; set; }
    public ConnectionEnvironment Environment { get; set; } = ConnectionEnvironment.Development;
    public string Notes { get; set; } = string.Empty;
    public string SearchableMetadata { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
    public DateTimeOffset? LastConnectedAtUtc { get; set; }
    public List<string> Tags { get; set; } = [];
    public RdpSettings Settings { get; set; } = new();
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Connection name is required.");
        else if (Name.Trim().Length > 200) errors.Add("Connection name cannot exceed 200 characters.");
        else if (Name.Any(char.IsControl)) errors.Add("Connection name cannot contain control characters.");
        if (string.IsNullOrWhiteSpace(Host)) errors.Add("Host is required.");
        else if (!IsValidConnectionHost(Host))
            errors.Add("Host must be a valid hostname or IP address without spaces, path characters, brackets, or an embedded port.");
        if (Port is < 1 or > 65535) errors.Add("Port must be between 1 and 65535.");
        if (UsernameHint?.Length > 320) errors.Add("Username hint cannot exceed 320 characters.");
        else if (UsernameHint?.Any(char.IsControl) == true) errors.Add("Username hint cannot contain control characters.");
        if (Notes.Length > 4000) errors.Add("Notes cannot exceed 4000 characters.");
        else if (Notes.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t')) errors.Add("Notes contain unsupported control characters.");
        if (Tags.Count > 50 || Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 64 || tag.Any(char.IsControl))) errors.Add("Use at most 50 non-empty tags of 64 characters or fewer without control characters.");
        if (Settings.ColorDepth is not (15 or 16 or 24 or 32)) errors.Add("Color depth must be 15, 16, 24, or 32 bits.");
        if (Settings.ReconnectAttempts is < 0 or > 20) errors.Add("Reconnect attempts must be between 0 and 20.");
        if (Settings.GatewayUsage != RdpGatewayUsage.Never && !IsValidConnectionHost(Settings.GatewayHost)) errors.Add("Gateway host is required when gateway use is enabled.");
        if (Settings.GatewayPort is < 1 or > 65535) errors.Add("Gateway port must be between 1 and 65535.");
        if (Settings.Kind == RdpConnectionKind.RemoteApp && string.IsNullOrWhiteSpace(Settings.RemoteAppProgram)) errors.Add("RemoteApp program is required.");
        if (Settings.SessionTimeoutMinutes is < 0 or > 10080) errors.Add("Session timeout must be between 0 and 10,080 minutes.");
        if (Settings.DisplayMode == RdpDisplayMode.SelectedMonitors && Settings.SelectedMonitorIds.Count == 0) errors.Add("Selected-monitor mode requires at least one monitor.");
        return errors;
    }

    private static bool IsValidConnectionHost(string host)
    {
        if (host.Length > 255 || host.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) || host.IndexOfAny(['/', '\\', '[', ']']) >= 0)
            return false;
        if (IPAddress.TryParse(host, out _)) return true;
        if (host.Contains(':')) return false;
        var trimmed = host.TrimEnd('.');
        if (trimmed.Length == 0) return false;
        foreach (var label in trimmed.Split('.'))
        {
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-') return false;
            if (label.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_')) return false;
        }
        return true;
    }
}

public sealed class ConnectionFolder
{
    public const int MaximumDepth = 32;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Folder name is required.");
        else if (Name.Trim().Length > 200) errors.Add("Folder name cannot exceed 200 characters.");
        else if (Name.Any(char.IsControl)) errors.Add("Folder name cannot contain control characters.");
        else if (Name.IndexOfAny(['/', '\\']) >= 0) errors.Add("Folder name cannot contain path separators.");
        if (ParentId == Id) errors.Add("A folder cannot be its own parent.");
        return errors;
    }
}

public sealed record FolderChoice(Guid? Id, string DisplayName);

public sealed class RdpSession
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid ConnectionId { get; init; }
    public SessionState State { get; set; } = SessionState.Created;
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAtUtc { get; set; }
    public string? DisconnectReason { get; set; }
}

public sealed class AuditEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public AuditEventType Type { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ConnectionId { get; set; }
    public Guid? SessionId { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string RedactedDetail { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = Guid.CreateVersion7().ToString("N");
}

public sealed record ImportCandidate(
    string Source,
    string Name,
    string Host,
    int Port,
    string? UsernameHint,
    IReadOnlyList<string> FolderPath,
    RdpSettings Settings,
    IReadOnlyList<string> Warnings);

public sealed record ImportPreview(
    string SourcePath,
    IReadOnlyList<ImportCandidate> Candidates,
    IReadOnlyList<string> Errors,
    int SecretFieldsDiscarded);

public sealed record DiagnosticResult(
    string Host,
    int Port,
    bool DnsSucceeded,
    IReadOnlyList<string> Addresses,
    bool PortReachable,
    TimeSpan Duration,
    string Message)
{
    public IReadOnlyList<DiagnosticAttempt> Attempts { get; init; } = [];
}

public sealed record DiagnosticAttempt(
    string Address,
    string AddressFamily,
    bool Succeeded,
    string Outcome,
    TimeSpan Duration);

public sealed record PolicyDecision(bool Allowed, string Reason, IReadOnlyList<string> AppliedPolicies);

public sealed record CredentialReference(string Provider, string ReferenceId);

public sealed record ConfigurationVersion(Guid Id, int Version, DateTimeOffset CreatedAtUtc, string CreatedBy, string Summary);
