using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AxMSTSCLib;
using MSTSCLib;
using NextRdp.Core;

namespace NextRdp.RdpHost;

public sealed class RdpSessionHostControl : UserControl, IRdpSessionHost
{
    private readonly AxHost client;
    private readonly System.Windows.Forms.Timer resizeTimer = new() { Interval = 250 };
    private bool disposed;
    private bool fullScreenMode;
    private int requestedDesktopWidth = 1600;
    private int requestedDesktopHeight = 900;
    private int lastAppliedDesktopWidth;
    private int lastAppliedDesktopHeight;
    private int configuredReconnectAttempts;
    private RdpSettings? configuredSettings;
    private string configuredConnectionName = string.Empty;
    private bool remoteAppStarted;

    public RdpSessionHostControl()
    {
        Dock = DockStyle.Fill;
        client = CreateNewestSupportedClient();
        AttachEvents();
        Resize += (_, _) => OnHostResized();
        resizeTimer.Tick += (_, _) =>
        {
            resizeTimer.Stop();
            ApplyConnectedDisplaySize();
        };
    }

    public event EventHandler<SessionState>? StateChanged;
    public event EventHandler<string>? DiagnosticMessage;
    public event EventHandler<RdpSessionDiagnostic>? DiagnosticRaised;
    public event EventHandler<RdpConnectionQuality>? ConnectionQualityChanged;

    public SessionState State { get; private set; } = SessionState.Created;

    public static RdpClientCapability ProbeCapability()
    {
        using var host = new RdpSessionHostControl();
        var ocx = host.client.GetOcx();
        return new(
            host.client.GetType().Name,
            ocx is IMsRdpClient10,
            ocx is IMsRdpClientNonScriptable5,
            ocx is IMsRdpClient10 { AdvancedSettings9: not null });
    }

    public void SetDesktopSize(int width, int height)
    {
        if (width < 1 || height < 1) return;
        requestedDesktopWidth = Math.Clamp(width, 640, 8192);
        requestedDesktopHeight = Math.Clamp(height, 480, 4320);
    }

    public byte[]? CaptureThumbnailPng(int maximumWidth = 240, int maximumHeight = 135)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (InvokeRequired) return (byte[]?)Invoke(() => CaptureThumbnailPng(maximumWidth, maximumHeight));
        if (State != SessionState.Connected || !Visible || client.Width < 1 || client.Height < 1) return null;
        maximumWidth = Math.Clamp(maximumWidth, 64, 640);
        maximumHeight = Math.Clamp(maximumHeight, 36, 360);

        try
        {
            var origin = client.PointToScreen(Point.Empty);
            using var capture = new Bitmap(client.Width, client.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(capture))
                graphics.CopyFromScreen(origin, Point.Empty, capture.Size, CopyPixelOperation.SourceCopy);

            var scale = Math.Min((double)maximumWidth / capture.Width, (double)maximumHeight / capture.Height);
            var width = Math.Max(1, (int)Math.Round(capture.Width * scale));
            var height = Math.Max(1, (int)Math.Round(capture.Height * scale));
            using var thumbnail = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(thumbnail))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(capture, new Rectangle(0, 0, width, height));
            }
            using var output = new MemoryStream();
            thumbnail.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        catch (Exception exception) when (exception is ExternalException or ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Thumbnails are an optional, memory-only convenience. Capture failures must not affect the live session
            // or enter logs/support packages, and connecting/reconnecting states are never captured.
            return null;
        }
    }

    public Task ConnectAsync(ConnectionProfile connection, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ConfigureForConnection(connection);

        SetState(SessionState.Connecting);
        try
        {
            GetRdpClient().Connect();
        }
        catch (COMException ex)
        {
            DiagnosticMessage?.Invoke(this, $"The Windows RDP client could not start the connection (0x{ex.HResult:X8}).");
            SetState(SessionState.Failed);
            throw new InvalidOperationException("The Windows RDP client could not start the connection. Check the target and RDP client installation.", ex);
        }
        return Task.CompletedTask;
    }

    internal void ConfigureForConnection(ConnectionProfile connection)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var errors = connection.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(connection));

        var ocx = client.GetOcx() ?? throw new InvalidOperationException("The Windows RDP control did not expose its COM instance.");
        var rdp = ocx as IMsRdpClient10 ?? throw new InvalidOperationException("The Windows RDP control does not support the required client interface.");
        var security = ocx as IMsRdpClientNonScriptable5 ?? throw new InvalidOperationException("The Windows RDP control does not support prompt-only credential configuration.");
        ValidateRepresentableSettings(connection.Settings, ocx);
        configuredSettings = connection.Settings;
        configuredConnectionName = connection.Name;
        remoteAppStarted = false;
        rdp.Server = connection.Host;
        rdp.UserName = connection.UsernameHint ?? string.Empty;
        rdp.ColorDepth = connection.Settings.ColorDepth;
        if (connection.Settings.DesktopWidth is { } configuredWidth && connection.Settings.DesktopHeight is { } configuredHeight)
            SetDesktopSize(configuredWidth, configuredHeight);
        rdp.DesktopWidth = requestedDesktopWidth;
        rdp.DesktopHeight = requestedDesktopHeight;
        lastAppliedDesktopWidth = requestedDesktopWidth;
        lastAppliedDesktopHeight = requestedDesktopHeight;
        var connectionBarText = connection.Environment == ConnectionEnvironment.Production
            ? $"PRODUCTION - NextRDP - {connection.Name}"
            : $"NextRDP - {connection.Name}";
        rdp.FullScreenTitle = connectionBarText;
        security.PromptForCredentials = true;
        security.PromptForCredsOnClient = true;
        security.AllowPromptingForCredentials = true;
        security.AllowCredentialSaving = false;
        security.ConnectionBarText = connectionBarText;
        security.DisableConnectionBar = false;
        IMsRdpClientAdvancedSettings8 advanced = rdp.AdvancedSettings9 ?? throw new InvalidOperationException("The Windows RDP control does not expose the required advanced settings.");
        advanced.RDPPort = connection.Port;
        advanced.EnableCredSspSupport = connection.Settings.RequireNetworkLevelAuthentication;
        advanced.ConnectToAdministerServer = connection.Settings.AdministrativeSession;
        advanced.LoadBalanceInfo = connection.Settings.BrokerLoadBalanceInfo;
        advanced.AuthenticationLevel = 2u;
        advanced.SmartSizing = connection.Settings.SmartSizing;
        advanced.RedirectClipboard = connection.Settings.RedirectClipboard;
        advanced.RedirectDrives = connection.Settings.RedirectDrives;
        advanced.RedirectPrinters = connection.Settings.RedirectPrinters;
        advanced.AudioRedirectionMode = connection.Settings.RedirectAudio ? 0u : 2u;
        advanced.AudioCaptureRedirectionMode = connection.Settings.RedirectMicrophone;
        advanced.RedirectPorts = connection.Settings.RedirectComPorts || connection.Settings.RedirectLptPorts;
        advanced.RedirectSmartCards = connection.Settings.RedirectSmartCards;
        advanced.RedirectDevices = connection.Settings.RedirectUsbDevices;
        security.RedirectDynamicDevices = connection.Settings.RedirectUsbDevices;
        security.RedirectDynamicDrives = connection.Settings.RedirectDrives;
        ConfigureCameraRedirection(ocx, connection.Settings.RedirectCamera);
        advanced.MinutesToIdleTimeout = connection.Settings.SessionTimeoutMinutes;
        ConfigurePerformance(advanced, connection.Settings.PerformancePreset);
        ConfigureKeyboard(rdp, advanced, connection.Settings.ShortcutRouting, connection.Settings.AllowSecureAttentionSequence);
        ConfigureGateway(rdp, connection.Settings);
        ConfigureDisplayMode(security, connection.Settings.DisplayMode);
        ConfigureRemoteApp(rdp, connection.Settings);
        advanced.EnableAutoReconnect = connection.Settings.ReconnectAttempts > 0;
        advanced.MaxReconnectAttempts = connection.Settings.ReconnectAttempts;
        configuredReconnectAttempts = connection.Settings.ReconnectAttempts;
        advanced.ContainerHandledFullScreen = 0;
        advanced.DisplayConnectionBar = true;
        advanced.PinConnectionBar = true;
        advanced.ConnectionBarShowMinimizeButton = true;
        advanced.ConnectionBarShowRestoreButton = true;
        advanced.ConnectionBarShowPinButton = true;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed) return Task.CompletedTask;
        try
        {
            var rdp = GetRdpClient();
            if (rdp.Connected != 0) rdp.Disconnect();
            else if (State is not (SessionState.Disconnected or SessionState.Failed)) SetState(SessionState.Disconnected);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            DiagnosticMessage?.Invoke(this, $"The Windows RDP client could not complete a graceful disconnect (0x{ex.HResult:X8}). The tab will still close and release its local control.");
            if (State is not (SessionState.Disconnected or SessionState.Failed)) SetState(SessionState.Disconnected);
        }
        return Task.CompletedTask;
    }

    public void SetFullScreen(bool enabled)
    {
        if (disposed) return;
        try
        {
            var rdp = GetRdpClient();
            if (rdp.Connected != 0)
            {
                if (enabled)
                {
                    fullScreenMode = true;
                    ApplyFullScreenDisplaySize();
                }

                rdp.FullScreen = enabled;

                if (!enabled)
                {
                    fullScreenMode = false;
                    ScheduleWindowedDisplayRestore();
                }
            }
            else DiagnosticMessage?.Invoke(this, "Full screen is available after the RDP session connects.");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            fullScreenMode = false;
            ScheduleWindowedDisplayRestore();
            DiagnosticMessage?.Invoke(this, $"The Windows RDP client could not change full-screen mode (0x{ex.HResult:X8}). The embedded session remains available.");
        }
    }

    public bool SendSecureAttentionSequence()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (InvokeRequired) return (bool)Invoke(SendSecureAttentionSequence);

        if (configuredSettings?.AllowSecureAttentionSequence != true)
        {
            DiagnosticMessage?.Invoke(this, "Ctrl+Alt+End is disabled by this connection's effective policy.");
            return false;
        }

        if (State != SessionState.Connected)
        {
            DiagnosticMessage?.Invoke(this, "Ctrl+Alt+End is available after the RDP session connects.");
            return false;
        }

        try
        {
            client.Focus();
            System.Windows.Forms.SendKeys.SendWait("^%{END}");
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ExternalException)
        {
            DiagnosticMessage?.Invoke(this, $"The Windows RDP client could not send Ctrl+Alt+End (0x{exception.HResult:X8}).");
            return false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposed) return;
        if (disposing)
        {
            resizeTimer.Stop();
            resizeTimer.Dispose();
            try { DisconnectAsync().GetAwaiter().GetResult(); } catch (InvalidOperationException) { }
            try { client.Dispose(); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
        }
        disposed = true;
        base.Dispose(disposing);
    }

    private AxHost CreateNewestSupportedClient()
    {
        var factories = new Func<AxHost>[]
        {
            static () => new AxMsRdpClient12NotSafeForScripting(),
            static () => new AxMsRdpClient11NotSafeForScripting(),
            static () => new AxMsRdpClient10NotSafeForScripting()
        };
        Exception? lastFailure = null;
        foreach (var factory in factories)
        {
            AxHost? candidate = null;
            try
            {
                candidate = factory();
                ((ISupportInitialize)candidate).BeginInit();
                candidate.Dock = DockStyle.Fill;
                Controls.Add(candidate);
                ((ISupportInitialize)candidate).EndInit();
                var ocx = candidate.GetOcx();
                if (ocx is not IMsRdpClient10 { AdvancedSettings9: not null } || ocx is not IMsRdpClientNonScriptable5)
                    throw new InvalidOperationException($"{candidate.GetType().Name} does not expose the required RDP client, advanced-settings, and prompt-only credential interfaces.");
                return candidate;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                lastFailure = ex;
                if (candidate is not null)
                {
                    Controls.Remove(candidate);
                    candidate.Dispose();
                }
            }
        }
        throw new PlatformNotSupportedException(
            "No compatible Microsoft Remote Desktop ActiveX control is available. Repair the Windows Remote Desktop client components and confirm that the required client, advanced-settings, and prompt-only credential interfaces are registered.",
            lastFailure);
    }

    private void AttachEvents()
    {
        Attach("OnConnecting", (_, _) => SetState(SessionState.Connecting));
        Attach("OnConnected", (_, _) =>
        {
            SetState(SessionState.Connected);
            StartRemoteAppIfConfigured();
        });
        Attach("OnAutoReconnecting", (_, args) =>
        {
            var progress = PrepareAutomaticReconnect(args);
            SetState(SessionState.Reconnecting);
            var attempt = progress.AttemptCount > 0 ? progress.AttemptCount.ToString(CultureInfo.InvariantCulture) : "unknown";
            var limit = configuredReconnectAttempts > 0 ? configuredReconnectAttempts.ToString(CultureInfo.InvariantCulture) : "configured limit";
            DiagnosticMessage?.Invoke(this, $"Automatic reconnect attempt {attempt} of {limit} is in progress.");
        });
        Attach("OnAutoReconnected", (_, _) => SetState(SessionState.Connected));
        Attach("OnEnterFullScreenMode", (_, _) =>
        {
            fullScreenMode = true;
            ApplyFullScreenDisplaySize();
        });
        Attach("OnLeaveFullScreenMode", (_, _) =>
        {
            fullScreenMode = false;
            ScheduleWindowedDisplayRestore();
        });
        Attach("OnDisconnected", (_, args) =>
        {
            var reason = ReadIntField(args, "discReason");
            var expected = RdpErrorMessages.IsExpectedDisconnect(reason);
            ReportDiagnostic(new RdpSessionDiagnostic(RdpErrorMessages.DisconnectCategory(reason), reason, RdpErrorMessages.Disconnect(reason), !expected));
            SetState(expected ? SessionState.Disconnected : SessionState.Failed);
        });
        Attach("OnFatalError", (_, args) =>
        {
            var code = ReadIntField(args, "errorCode");
            ReportDiagnostic(new RdpSessionDiagnostic("fatal-client-error", code, RdpErrorMessages.Fatal(code), true));
            SetState(SessionState.Failed);
        });
        Attach("OnLogonError", (_, args) =>
        {
            var code = ReadIntField(args, "lError");
            ReportDiagnostic(new RdpSessionDiagnostic(RdpErrorMessages.LogonCategory(code), code, RdpErrorMessages.Logon(code), RdpErrorMessages.IsTerminalLogonError(code)));
        });
        Attach("OnAuthenticationWarningDisplayed", (_, _) => ReportDiagnostic(new RdpSessionDiagnostic(
            "certificate-warning", 0, "Windows displayed a certificate identity warning. Verify that the certificate and target name match the intended server before continuing.", false)));
        TryAttach("OnNetworkStatusChanged", (_, args) =>
        {
            var quality = CreateConnectionQuality(
                ReadUIntField(args, "qualityLevel"),
                ReadUIntField(args, "bandwidth"),
                ReadUIntField(args, "rtt"),
                DateTimeOffset.UtcNow);
            ConnectionQualityChanged?.Invoke(this, quality);
            if (configuredSettings?.ShowConnectionQuality == true)
                DiagnosticMessage?.Invoke(this, FormatConnectionQuality(quality));
        });
    }

    internal static RdpConnectionQuality CreateConnectionQuality(uint qualityLevel, uint bandwidthKbps, uint roundTripTimeMilliseconds, DateTimeOffset measuredAtUtc) =>
        new(qualityLevel, bandwidthKbps, roundTripTimeMilliseconds, null, measuredAtUtc);

    internal static string FormatConnectionQuality(RdpConnectionQuality quality) =>
        quality.RoundTripTimeMilliseconds == uint.MaxValue
            ? "Connection quality is unavailable from the Windows RDP client."
            : $"RDP quality level {quality.QualityLevel}; latency {quality.RoundTripTimeMilliseconds} ms; bandwidth {quality.BandwidthKbps} Kbps; packet loss unavailable from the Microsoft control.";

    private void ReportDiagnostic(RdpSessionDiagnostic diagnostic)
    {
        DiagnosticMessage?.Invoke(this, diagnostic.Message);
        DiagnosticRaised?.Invoke(this, diagnostic);
    }

    private void Attach(string eventName, Action<object?, object?> callback)
    {
        var eventInfo = client.GetType().GetEvent(eventName) ?? throw new MissingMemberException(client.GetType().FullName, eventName);
        var invoke = eventInfo.EventHandlerType?.GetMethod("Invoke") ?? throw new InvalidOperationException($"Event {eventName} has no handler signature.");
        var parameters = invoke.GetParameters().Select(x => Expression.Parameter(x.ParameterType, x.Name)).ToArray();
        var target = Expression.Constant(callback);
        var call = Expression.Call(target, typeof(Action<object?, object?>).GetMethod(nameof(Action<object?, object?>.Invoke))!,
            Expression.Convert(parameters[0], typeof(object)), Expression.Convert(parameters[1], typeof(object)));
        var handler = Expression.Lambda(eventInfo.EventHandlerType!, call, parameters).Compile();
        eventInfo.AddEventHandler(client, handler);
    }

    private void TryAttach(string eventName, Action<object?, object?> callback)
    {
        if (client.GetType().GetEvent(eventName) is not null) Attach(eventName, callback);
    }

    private static int ReadIntField(object? value, string name) =>
        value?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(value) as int? ?? -1;

    private static uint ReadUIntField(object? value, string name)
    {
        var raw = value?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(value);
        return raw switch { uint result => result, int result when result >= 0 => (uint)result, _ => uint.MaxValue };
    }

    private static void ValidateRepresentableSettings(RdpSettings settings, object ocx)
    {
        if (settings.ClipboardDirection is RdpTransferDirection.ClientToRemote or RdpTransferDirection.RemoteToClient)
            throw new NotSupportedException("This Windows RDP control cannot continuously enforce one-way clipboard redirection. Choose Disabled or Bidirectional.");
        if (settings.FileTransferDirection is RdpTransferDirection.ClientToRemote or RdpTransferDirection.RemoteToClient)
            throw new NotSupportedException("This Windows RDP control cannot enforce one-way drive/file redirection. Choose Disabled or Bidirectional.");
        if (settings.RedirectComPorts != settings.RedirectLptPorts)
            throw new NotSupportedException("This Windows RDP control exposes COM and LPT redirection as one combined setting. Enable or disable both together.");
        if (settings.RedirectCamera && ocx is not IMsRdpClientNonScriptable7)
            throw new NotSupportedException("Camera redirection requires a newer Microsoft RDP control on this computer.");
        if (settings.DisplayMode == RdpDisplayMode.SelectedMonitors && settings.SelectedMonitorIds.Count == 0)
            throw new NotSupportedException("Selected-monitor mode requires at least one available monitor.");
    }

    private static void ConfigureCameraRedirection(object ocx, bool enabled)
    {
        if (ocx is not IMsRdpClientNonScriptable7 cameraHost) return;
        cameraHost.CameraRedirConfigCollection.RedirectByDefault = enabled;
        cameraHost.CameraRedirConfigCollection.EncodeVideo = true;
    }

    private static void ConfigureGateway(IMsRdpClient10 rdp, RdpSettings settings)
    {
        var transport = rdp.TransportSettings4 ?? throw new InvalidOperationException("The Windows RDP control does not expose gateway transport settings.");
        transport.GatewayUsageMethod = settings.GatewayUsage switch
        {
            RdpGatewayUsage.Never => 0u,
            RdpGatewayUsage.Always => 1u,
            RdpGatewayUsage.Detect => 2u,
            _ => 0u
        };
        transport.GatewayProfileUsageMethod = 1u;
        transport.GatewayHostname = settings.GatewayUsage == RdpGatewayUsage.Never
            ? string.Empty
            : settings.GatewayPort == 443 ? settings.GatewayHost : $"{settings.GatewayHost}:{settings.GatewayPort}";
    }

    private static void ConfigureRemoteApp(IMsRdpClient10 rdp, RdpSettings settings)
    {
        var remote = rdp.RemoteProgram2;
        remote.RemoteProgramMode = settings.Kind == RdpConnectionKind.RemoteApp;
        if (settings.Kind != RdpConnectionKind.RemoteApp) return;
        remote.RemoteApplicationName = settings.RemoteAppProgram;
        remote.RemoteApplicationProgram = settings.RemoteAppProgram;
        remote.RemoteApplicationArgs = settings.RemoteAppArguments;
    }

    private static void ConfigureDisplayMode(IMsRdpClientNonScriptable5 security, RdpDisplayMode mode) =>
        security.UseMultimon = mode == RdpDisplayMode.AllMonitors;

    private static void ConfigureKeyboard(IMsRdpClient10 rdp, IMsRdpClientAdvancedSettings8 advanced, RdpShortcutRouting routing, bool allowSecureAttentionSequence)
    {
        rdp.SecuredSettings3.KeyboardHookMode = routing switch { RdpShortcutRouting.Local => 0, RdpShortcutRouting.RemoteWhenFocused => 1, _ => 2 };
        advanced.EnableWindowsKey = routing == RdpShortcutRouting.Local ? 0 : 1;
        advanced.HotKeyCtrlAltDel = (int)Keys.End;
        advanced.DisableCtrlAltDel = allowSecureAttentionSequence ? 0 : 1;
    }

    private static void ConfigurePerformance(IMsRdpClientAdvancedSettings8 advanced, RdpPerformancePreset preset)
    {
        advanced.BandwidthDetection = preset == RdpPerformancePreset.Auto;
        advanced.NetworkConnectionType = preset switch
        {
            RdpPerformancePreset.Lan => 6u,
            RdpPerformancePreset.Broadband => 4u,
            RdpPerformancePreset.LowBandwidth => 2u,
            _ => 7u
        };
        advanced.PerformanceFlags = preset switch
        {
            RdpPerformancePreset.Lan => 0,
            RdpPerformancePreset.Broadband => 0x1 | 0x2 | 0x4,
            RdpPerformancePreset.LowBandwidth => 0x1 | 0x2 | 0x4 | 0x8 | 0x20 | 0x40 | 0x80,
            _ => 0
        };
    }

    private void StartRemoteAppIfConfigured()
    {
        if (remoteAppStarted || configuredSettings is not { Kind: RdpConnectionKind.RemoteApp } settings) return;
        try
        {
            GetRdpClient().RemoteProgram.ServerStartProgram(settings.RemoteAppProgram, string.Empty, settings.RemoteAppWorkingDirectory,
                true, settings.RemoteAppArguments, true);
            remoteAppStarted = true;
        }
        catch (COMException exception)
        {
            DiagnosticMessage?.Invoke(this, $"The RemoteApp program could not start (0x{exception.HResult:X8}). Confirm the published alias and Connection Broker settings.");
            SetState(SessionState.Failed);
        }
    }

    internal static AutoReconnectProgress PrepareAutomaticReconnect(object? eventArgs)
    {
        if (eventArgs is not AxMSTSCLib.IMsTscAxEvents_OnAutoReconnectingEvent reconnect)
            return new AutoReconnectProgress(-1, -1, false);

        reconnect.pArcContinueStatus = AutoReconnectContinueState.autoReconnectContinueAutomatic;
        return new AutoReconnectProgress(reconnect.disconnectReason, reconnect.attemptCount, true);
    }

    private IMsRdpClient10 GetRdpClient() =>
        client.GetOcx() as IMsRdpClient10 ?? throw new InvalidOperationException("The Windows RDP control does not support the required client interface.");

    internal (int Width, int Height) GetConfiguredDesktopSize()
    {
        var rdp = GetRdpClient();
        return (rdp.DesktopWidth, rdp.DesktopHeight);
    }

    internal ConfiguredRdpSnapshot GetConfiguredSnapshot()
    {
        var rdp = GetRdpClient();
        var security = client.GetOcx() as IMsRdpClientNonScriptable5 ?? throw new InvalidOperationException("The Windows RDP control does not support prompt-only credential configuration.");
        var advanced = rdp.AdvancedSettings9 ?? throw new InvalidOperationException("The Windows RDP control does not expose the required advanced settings.");
        return new ConfiguredRdpSnapshot(
            rdp.Server,
            advanced.RDPPort,
            rdp.ColorDepth,
            advanced.EnableCredSspSupport,
            advanced.SmartSizing,
            advanced.RedirectClipboard,
            advanced.RedirectDrives,
            advanced.RedirectPrinters,
            advanced.AudioRedirectionMode == 0,
            advanced.EnableAutoReconnect,
            advanced.MaxReconnectAttempts,
            security.PromptForCredentials,
            security.PromptForCredsOnClient,
            security.AllowPromptingForCredentials,
            security.AllowCredentialSaving,
            security.ConnectionBarText,
            advanced.ContainerHandledFullScreen,
            advanced.DisplayConnectionBar,
            advanced.PinConnectionBar,
            advanced.ConnectionBarShowMinimizeButton,
            advanced.ConnectionBarShowRestoreButton,
            advanced.ConnectionBarShowPinButton,
            rdp.DesktopWidth,
            rdp.DesktopHeight,
            advanced.ConnectToAdministerServer,
            advanced.LoadBalanceInfo,
            advanced.AudioCaptureRedirectionMode,
            (client.GetOcx() as IMsRdpClientNonScriptable7)?.CameraRedirConfigCollection.RedirectByDefault ?? false,
            advanced.RedirectPorts,
            advanced.RedirectSmartCards,
            advanced.RedirectDevices,
            advanced.MinutesToIdleTimeout,
            configuredSettings?.PerformancePreset == RdpPerformancePreset.Auto,
            configuredSettings?.PerformancePreset switch { RdpPerformancePreset.Lan => 6u, RdpPerformancePreset.Broadband => 4u, RdpPerformancePreset.LowBandwidth => 2u, _ => 0u },
            rdp.SecuredSettings3.KeyboardHookMode,
            advanced.EnableWindowsKey,
            advanced.HotKeyCtrlAltDel,
            advanced.DisableCtrlAltDel,
            security.UseMultimon);
    }

    private void OnHostResized()
    {
        if (fullScreenMode) return;
        SetDesktopSize(ClientSize.Width, ClientSize.Height);
        if (State != SessionState.Connected) return;
        resizeTimer.Stop();
        resizeTimer.Start();
    }

    private void ApplyFullScreenDisplaySize()
    {
        if (disposed || State != SessionState.Connected) return;
        var bounds = Screen.FromControl(client).Bounds;
        var size = ResolveFullScreenDesktopSize(bounds.Width, bounds.Height);
        ApplyConnectedDisplaySize(size.Width, size.Height, "full-screen");
    }

    private void ScheduleWindowedDisplayRestore()
    {
        if (disposed || State != SessionState.Connected) return;
        void Restore()
        {
            if (disposed || fullScreenMode || State != SessionState.Connected) return;
            SetDesktopSize(ClientSize.Width, ClientSize.Height);
            ApplyConnectedDisplaySize(requestedDesktopWidth, requestedDesktopHeight, "embedded");
        }

        if (IsHandleCreated) BeginInvoke((Action)Restore);
        else Restore();
    }

    private void ApplyConnectedDisplaySize() =>
        ApplyConnectedDisplaySize(requestedDesktopWidth, requestedDesktopHeight, "embedded");

    private void ApplyConnectedDisplaySize(int width, int height, string mode)
    {
        if (disposed || State != SessionState.Connected) return;
        var size = ClampDesktopSize(width, height);
        if (size.Width == lastAppliedDesktopWidth && size.Height == lastAppliedDesktopHeight) return;
        try
        {
            var dpi = Math.Max(DeviceDpi, 96);
            var physicalWidth = Math.Max(1u, (uint)Math.Round(size.Width * 25.4 / dpi));
            var physicalHeight = Math.Max(1u, (uint)Math.Round(size.Height * 25.4 / dpi));
            GetRdpClient().UpdateSessionDisplaySettings(
                (uint)size.Width,
                (uint)size.Height,
                physicalWidth,
                physicalHeight,
                0,
                100,
                100);
            lastAppliedDesktopWidth = size.Width;
            lastAppliedDesktopHeight = size.Height;
        }
        catch (COMException ex)
        {
            DiagnosticMessage?.Invoke(this, $"The remote session could not apply the {mode} display size (0x{ex.HResult:X8}); smart sizing remains active.");
        }
    }

    internal static (int Width, int Height) ResolveFullScreenDesktopSize(int screenWidth, int screenHeight) =>
        ClampDesktopSize(screenWidth, screenHeight);

    private static (int Width, int Height) ClampDesktopSize(int width, int height) =>
        (Math.Clamp(width, 640, 8192), Math.Clamp(height, 480, 4320));

    private void SetState(SessionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, state);
    }
}

internal sealed record ConfiguredRdpSnapshot(
    string Host,
    int Port,
    int ColorDepth,
    bool RequireNla,
    bool SmartSizing,
    bool Clipboard,
    bool Drives,
    bool Printers,
    bool Audio,
    bool AutoReconnect,
    int ReconnectAttempts,
    bool PromptForCredentials,
    bool PromptForCredsOnClient,
    bool AllowPromptingForCredentials,
    bool AllowCredentialSaving,
    string ConnectionBarText,
    int ContainerHandledFullScreen,
    bool DisplayConnectionBar,
    bool PinConnectionBar,
    bool ConnectionBarShowMinimizeButton,
    bool ConnectionBarShowRestoreButton,
    bool ConnectionBarShowPinButton,
    int DesktopWidth,
    int DesktopHeight,
    bool AdministrativeSession,
    string BrokerLoadBalanceInfo,
    bool Microphone,
    bool Camera,
    bool Ports,
    bool SmartCards,
    bool UsbDevices,
    int IdleTimeoutMinutes,
    bool BandwidthDetection,
    uint NetworkConnectionType,
    int KeyboardHookMode,
    int WindowsKeyEnabled,
    int SecureAttentionHotKey,
    int SecureAttentionDisabled,
    bool UseMultimon);

internal sealed record AutoReconnectProgress(int DisconnectReason, int AttemptCount, bool ContinuationWasSet);

public sealed record RdpClientCapability(
    string ControlName,
    bool SupportsRequiredClientInterface,
    bool SupportsPromptOnlyCredentials,
    bool SupportsAdvancedSettings);

public sealed record RdpConnectionQuality(uint QualityLevel, uint BandwidthKbps, uint RoundTripTimeMilliseconds, double? PacketLossPercent, DateTimeOffset MeasuredAtUtc);

public sealed record RdpSessionDiagnostic(string Category, int Code, string Message, bool IsFailure);
