namespace NextRdp.Platform.Domain;

public sealed class ManagedRdpSettings
{
    public ManagedConnectionKind Kind { get; set; }
    public bool RequireNetworkLevelAuthentication { get; set; } = true;
    public bool AdministrativeSession { get; set; }
    public string BrokerLoadBalanceInfo { get; set; } = string.Empty;
    public GatewayUsage GatewayUsage { get; set; }
    public string GatewayHost { get; set; } = string.Empty;
    public int GatewayPort { get; set; } = 443;
    public string RemoteAppProgram { get; set; } = string.Empty;
    public string RemoteAppWorkingDirectory { get; set; } = string.Empty;
    public string RemoteAppArguments { get; set; } = string.Empty;
    public SessionDisplayMode DisplayMode { get; set; } = SessionDisplayMode.Embedded;
    public List<uint> SelectedMonitorIds { get; set; } = [];
    public int? DesktopWidth { get; set; }
    public int? DesktopHeight { get; set; }
    public int ColorDepth { get; set; } = 32;
    public PerformancePreset PerformancePreset { get; set; }
    public bool ShowConnectionQuality { get; set; } = true;
    public ThumbnailMode ThumbnailMode { get; set; } = ThumbnailMode.Disabled;
    public ShortcutRouting ShortcutRouting { get; set; } = ShortcutRouting.RemoteInFullScreen;
    public bool AllowSecureAttentionSequence { get; set; } = true;
    public bool RedirectMicrophone { get; set; }
    public bool RedirectCamera { get; set; }
    public bool RedirectComPorts { get; set; }
    public bool RedirectLptPorts { get; set; }
    public bool RedirectUsbDevices { get; set; }
    public bool RedirectSmartCards { get; set; }
    public bool RedirectPrinters { get; set; }
    public bool RedirectDrives { get; set; }
    public bool RedirectAudio { get; set; } = true;
    public TransferDirection ClipboardDirection { get; set; } = TransferDirection.Bidirectional;
    public TransferDirection FileTransferDirection { get; set; } = TransferDirection.Disabled;
    public int SessionTimeoutMinutes { get; set; }
    public int ReconnectAttempts { get; set; } = 5;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (GatewayUsage != GatewayUsage.Never && !PlatformValidation.IsValidHost(GatewayHost)) errors.Add("Gateway host is required when gateway use is enabled.");
        if (GatewayPort is < 1 or > 65535) errors.Add("Gateway port must be between 1 and 65535.");
        if (BrokerLoadBalanceInfo.Length > 2048 || BrokerLoadBalanceInfo.Any(char.IsControl)) errors.Add("Connection Broker load-balance information is invalid.");
        if (Kind == ManagedConnectionKind.RemoteApp && string.IsNullOrWhiteSpace(RemoteAppProgram)) errors.Add("RemoteApp program is required for a RemoteApp connection.");
        if (RemoteAppProgram.Length > 1024 || RemoteAppWorkingDirectory.Length > 1024 || RemoteAppArguments.Length > 4096)
            errors.Add("RemoteApp program, working directory, or arguments exceed the supported length.");
        if (RemoteAppProgram.Any(char.IsControl) || RemoteAppWorkingDirectory.Any(char.IsControl) || RemoteAppArguments.Any(character => char.IsControl(character) && character is not '\t'))
            errors.Add("RemoteApp values cannot contain control characters.");
        if (SelectedMonitorIds.Count > 16 || SelectedMonitorIds.Count != SelectedMonitorIds.Distinct().Count()) errors.Add("Select at most 16 unique monitor identifiers.");
        if (DisplayMode == SessionDisplayMode.SelectedMonitors && SelectedMonitorIds.Count == 0) errors.Add("Selected-monitor mode requires at least one monitor.");
        if (DesktopWidth is not null && DesktopWidth is < 320 or > 16384) errors.Add("Desktop width must be between 320 and 16,384 pixels.");
        if (DesktopHeight is not null && DesktopHeight is < 200 or > 16384) errors.Add("Desktop height must be between 200 and 16,384 pixels.");
        if ((DesktopWidth is null) != (DesktopHeight is null)) errors.Add("Desktop width and height must be specified together.");
        if (ColorDepth is not (15 or 16 or 24 or 32)) errors.Add("Color depth must be 15, 16, 24, or 32 bits.");
        if (SessionTimeoutMinutes is < 0 or > 10080) errors.Add("Session timeout must be between 0 and 10,080 minutes.");
        if (ReconnectAttempts is < 0 or > 20) errors.Add("Reconnect attempts must be between 0 and 20.");
        return errors;
    }
}
