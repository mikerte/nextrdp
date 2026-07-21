using NextRdp.RdpHost;
using NextRdp.Core;
using AxMSTSCLib;
using MSTSCLib;
using Xunit;

namespace NextRdp.RdpHost.Tests;

public sealed class RdpControlActivationTests
{
    // Hosted Windows runners can take substantially longer to initialize the native RDP
    // COM control on a cold desktop. Keep the operation bounded without allowing a timed-out
    // STA worker to overlap the next control test and invalidate its RCW.
    private static readonly TimeSpan StaOperationTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public void ThumbnailCaptureIsSuppressedBeforeConnectedState()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new RdpSessionHostControl();
                Assert.Null(control.CaptureThumbnailPng());
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(StaOperationTimeout), "RDP thumbnail privacy test timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public void NewestAvailableRdpControlCanBeCreatedOnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new RdpSessionHostControl();
                Assert.NotNull(control);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(StaOperationTimeout), "RDP ActiveX control activation timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public void PromptOnlyConnectionConfigurationIsSupportedBySelectedControl()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new RdpSessionHostControl();
                control.SetDesktopSize(1440, 900);
                control.ConfigureForConnection(new ConnectionProfile
                {
                    Name = "Configuration regression target",
                    Host = "192.0.2.1",
                    Port = 3390,
                    UsernameHint = "TestUser",
                    Environment = ConnectionEnvironment.Production,
                    Settings = new RdpSettings
                    {
                        RequireNetworkLevelAuthentication = true,
                        SmartSizing = true,
                        RedirectClipboard = false,
                        RedirectDrives = true,
                        RedirectPrinters = true,
                        RedirectAudio = false,
                        ColorDepth = 24,
                        ReconnectAttempts = 8
                    }
                });
                Assert.Equal((1440, 900), control.GetConfiguredDesktopSize());
                var snapshot = control.GetConfiguredSnapshot();
                Assert.Equal("192.0.2.1", snapshot.Host);
                Assert.Equal(3390, snapshot.Port);
                Assert.Equal(24, snapshot.ColorDepth);
                Assert.True(snapshot.RequireNla);
                Assert.True(snapshot.SmartSizing);
                Assert.False(snapshot.Clipboard);
                Assert.True(snapshot.Drives);
                Assert.True(snapshot.Printers);
                Assert.False(snapshot.Audio);
                Assert.True(snapshot.AutoReconnect);
                Assert.Equal(8, snapshot.ReconnectAttempts);
                Assert.True(snapshot.PromptForCredentials);
                Assert.True(snapshot.PromptForCredsOnClient);
                Assert.True(snapshot.AllowPromptingForCredentials);
                Assert.False(snapshot.AllowCredentialSaving);
                Assert.Equal("PRODUCTION - NextRDP - Configuration regression target", snapshot.ConnectionBarText);
                Assert.Equal(0, snapshot.ContainerHandledFullScreen);
                Assert.True(snapshot.DisplayConnectionBar);
                Assert.True(snapshot.PinConnectionBar);
                Assert.True(snapshot.ConnectionBarShowMinimizeButton);
                Assert.True(snapshot.ConnectionBarShowRestoreButton);
                Assert.True(snapshot.ConnectionBarShowPinButton);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(StaOperationTimeout), "RDP connection configuration timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public void RedirectionSettingsCanBeEnabledAndDisabledIndependently()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new RdpSessionHostControl();
                control.ConfigureForConnection(new ConnectionProfile
                {
                    Name = "Opposite redirection regression target",
                    Host = "192.0.2.2",
                    Settings = new RdpSettings
                    {
                        RedirectClipboard = true,
                        RedirectDrives = false,
                        RedirectPrinters = false,
                        RedirectAudio = true
                    }
                });

                var snapshot = control.GetConfiguredSnapshot();
                Assert.True(snapshot.Clipboard);
                Assert.False(snapshot.Drives);
                Assert.False(snapshot.Printers);
                Assert.True(snapshot.Audio);
            }
            catch (Exception ex) { failure = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(StaOperationTimeout), "RDP redirection configuration timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public void Phase2DisplayInputDeviceAndBrokerSettingsMapToTheMicrosoftControl()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new RdpSessionHostControl();
                control.ConfigureForConnection(new ConnectionProfile
                {
                    Name = "Phase 2 settings",
                    Host = "192.0.2.5",
                    Settings = new RdpSettings
                    {
                        DesktopWidth = 1600,
                        DesktopHeight = 900,
                        AdministrativeSession = true,
                        BrokerLoadBalanceInfo = "tsv://MS Terminal Services Plugin.1.pool",
                        DisplayMode = RdpDisplayMode.AllMonitors,
                        RedirectMicrophone = true,
                        RedirectCamera = true,
                        RedirectComPorts = true,
                        RedirectLptPorts = true,
                        RedirectSmartCards = true,
                        RedirectUsbDevices = true,
                        SessionTimeoutMinutes = 45,
                        PerformancePreset = RdpPerformancePreset.Lan,
                        ShortcutRouting = RdpShortcutRouting.RemoteWhenFocused,
                        AllowSecureAttentionSequence = false
                    }
                });
                var snapshot = control.GetConfiguredSnapshot();
                Assert.Equal(1600, snapshot.DesktopWidth);
                Assert.Equal(900, snapshot.DesktopHeight);
                Assert.True(snapshot.AdministrativeSession);
                Assert.StartsWith("tsv://", snapshot.BrokerLoadBalanceInfo, StringComparison.Ordinal);
                Assert.True(snapshot.Microphone);
                Assert.True(snapshot.Camera);
                Assert.True(snapshot.Ports);
                Assert.True(snapshot.SmartCards);
                Assert.True(snapshot.UsbDevices);
                Assert.Equal(45, snapshot.IdleTimeoutMinutes);
                Assert.False(snapshot.BandwidthDetection);
                Assert.Equal(6u, snapshot.NetworkConnectionType);
                Assert.Equal(1, snapshot.KeyboardHookMode);
                Assert.Equal(1, snapshot.WindowsKeyEnabled);
                Assert.Equal((int)System.Windows.Forms.Keys.End, snapshot.SecureAttentionHotKey);
                Assert.Equal(1, snapshot.SecureAttentionDisabled);
                Assert.True(snapshot.UseMultimon);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(StaOperationTimeout), "Phase 2 RDP control configuration timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public void SecureAttentionActionIsBoundedUntilTheSessionConnects()
    {
        Exception? failure = null;
        string? diagnostic = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new RdpSessionHostControl();
                control.ConfigureForConnection(new ConnectionProfile
                {
                    Name = "Secure attention",
                    Host = "192.0.2.9",
                    Settings = new RdpSettings { AllowSecureAttentionSequence = true }
                });
                control.DiagnosticMessage += (_, message) => diagnostic = message;
                Assert.False(control.SendSecureAttentionSequence());
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(StaOperationTimeout));
        Assert.Null(failure);
        Assert.Contains("after", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnsupportedOneWayTransferControlsFailVisibly()
    {
        Exception? observed = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new RdpSessionHostControl();
                control.ConfigureForConnection(new ConnectionProfile { Name = "Directional transfer", Host = "192.0.2.7", Settings = new RdpSettings { ClipboardDirection = RdpTransferDirection.ClientToRemote } });
            }
            catch (Exception exception) { observed = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(StaOperationTimeout));
        Assert.IsType<NotSupportedException>(observed);
        Assert.Contains("direction", observed!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IndependentlyConfiguredComAndLptRedirectionFailsVisibly()
    {
        Exception? observed = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new RdpSessionHostControl();
                control.ConfigureForConnection(new ConnectionProfile
                {
                    Name = "Port mismatch",
                    Host = "192.0.2.8",
                    Settings = new RdpSettings { RedirectComPorts = true, RedirectLptPorts = false }
                });
            }
            catch (Exception exception) { observed = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(StaOperationTimeout));
        Assert.IsType<NotSupportedException>(observed);
        Assert.Contains("COM and LPT", observed!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AutoReconnectEventExplicitlyContinuesAndPreservesAttemptDetails()
    {
        var eventArgs = new IMsTscAxEvents_OnAutoReconnectingEvent(260, 3)
        {
            pArcContinueStatus = AutoReconnectContinueState.autoReconnectContinueStop
        };

        var result = RdpSessionHostControl.PrepareAutomaticReconnect(eventArgs);

        Assert.Equal(AutoReconnectContinueState.autoReconnectContinueAutomatic, eventArgs.pArcContinueStatus);
        Assert.Equal(260, result.DisconnectReason);
        Assert.Equal(3, result.AttemptCount);
        Assert.True(result.ContinuationWasSet);
    }

    [Fact]
    public void ConnectionQualityKeepsUnavailablePacketLossExplicitAndPrivacySafe()
    {
        var measuredAt = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var quality = RdpSessionHostControl.CreateConnectionQuality(3, 12000, 42, measuredAt);
        var message = RdpSessionHostControl.FormatConnectionQuality(quality);

        Assert.Equal(3u, quality.QualityLevel);
        Assert.Equal(12000u, quality.BandwidthKbps);
        Assert.Equal(42u, quality.RoundTripTimeMilliseconds);
        Assert.Null(quality.PacketLossPercent);
        Assert.Equal(measuredAt, quality.MeasuredAtUtc);
        Assert.Contains("latency 42 ms", message, StringComparison.Ordinal);
        Assert.Contains("packet loss unavailable", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("host", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "Connection quality is unavailable from the Windows RDP client.",
            RdpSessionHostControl.FormatConnectionQuality(quality with { RoundTripTimeMilliseconds = uint.MaxValue }));
    }

    [Fact]
    public void CapabilityProbeConfirmsRequiredClientAndCredentialInterfaces()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var capability = RdpSessionHostControl.ProbeCapability();
                Assert.Contains("RdpClient", capability.ControlName, StringComparison.OrdinalIgnoreCase);
                Assert.True(capability.SupportsRequiredClientInterface);
                Assert.True(capability.SupportsPromptOnlyCredentials);
                Assert.True(capability.SupportsAdvancedSettings);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(StaOperationTimeout), "RDP capability probe timed out.");
        Assert.Null(failure);
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(320, 200, 640, 480)]
    [InlineData(10000, 5000, 8192, 4320)]
    public void FullScreenDesktopSizeUsesTheMonitorResolutionWithinRdpLimits(
        int screenWidth,
        int screenHeight,
        int expectedWidth,
        int expectedHeight)
    {
        Assert.Equal(
            (expectedWidth, expectedHeight),
            RdpSessionHostControl.ResolveFullScreenDesktopSize(screenWidth, screenHeight));
    }

    [Fact]
    public void ClosingBeforeConnectionProducesATerminalDisconnectedState()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new RdpSessionHostControl();
                var observedStates = new List<SessionState>();
                control.StateChanged += (_, state) => observedStates.Add(state);

                control.DisconnectAsync().GetAwaiter().GetResult();

                Assert.Equal(SessionState.Disconnected, control.State);
                Assert.Equal([SessionState.Disconnected], observedStates);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(StaOperationTimeout), "RDP pre-connection close timed out.");
        Assert.Null(failure);
    }

    [Theory]
    [InlineData(3, "idle timeout")]
    [InlineData(7, "denied")]
    [InlineData(10, "fresh credentials")]
    public void DisconnectReasonsProduceActionableMessages(int reason, string expected)
    {
        Assert.Contains(expected, RdpErrorMessages.Disconnect(reason), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(11)]
    [InlineData(12)]
    public void UserOrApiDisconnectsAreNotReportedAsFailures(int reason) =>
        Assert.True(RdpErrorMessages.IsExpectedDisconnect(reason));

    [Theory]
    [InlineData(0, "username or password")]
    [InlineData(1, "expired")]
    [InlineData(unchecked((int)0xC000006E), "account restriction")]
    [InlineData(unchecked((int)0xC0000224), "must be changed")]
    [InlineData(unchecked((int)0xC0000234), "locked")]
    public void LogonErrorsDistinguishCredentialAndAccountStates(int error, string expected) =>
        Assert.Contains(expected, RdpErrorMessages.Logon(error), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData(0, "credentials-rejected")]
    [InlineData(unchecked((int)0xC0000071), "password-expired")]
    [InlineData(unchecked((int)0xC0000072), "account-disabled")]
    [InlineData(unchecked((int)0xC0000224), "password-change-required")]
    [InlineData(unchecked((int)0xC0000234), "account-locked")]
    public void LogonErrorsExposeStableRedactedAuditCategories(int error, string expected)
    {
        Assert.Equal(expected, RdpErrorMessages.LogonCategory(error));
        Assert.True(RdpErrorMessages.IsTerminalLogonError(error));
    }

    [Theory]
    [InlineData(3, "idle-timeout")]
    [InlineData(4, "logon-timeout")]
    [InlineData(7, "access-denied")]
    [InlineData(10, "fresh-credentials-required")]
    public void DisconnectReasonsExposeStableRedactedAuditCategories(int reason, string expected) =>
        Assert.Equal(expected, RdpErrorMessages.DisconnectCategory(reason));

    [Fact]
    public void UnknownLogonErrorsRetainUnsignedHexCodeWithoutCredentials()
    {
        var message = RdpErrorMessages.Logon(unchecked((int)0x81234567));

        Assert.Contains("0x81234567", message, StringComparison.Ordinal);
        Assert.DoesNotContain("password=", message, StringComparison.OrdinalIgnoreCase);
    }

}
