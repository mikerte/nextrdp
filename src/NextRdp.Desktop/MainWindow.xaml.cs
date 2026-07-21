using System.IO;
using System.Net.Http;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Data.Common;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using Microsoft.Win32;
using NextRdp.Core;
using NextRdp.RdpHost;
using NextRdp.Platform.Application;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace NextRdp.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private bool sessionCleanupInProgress;
    private bool sessionCleanupCompleted;
    private readonly Dictionary<Guid, Window> detachedSessionWindows = [];
    private readonly HashSet<Guid> detachedWindowTeardown = [];
    private readonly Dictionary<Guid, DispatcherTimer> thumbnailTimers = [];

    internal bool IsControlledShutdownInProgress => sessionCleanupInProgress || sessionCleanupCompleted;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            foreach (var timer in thumbnailTimers.Values) timer.Stop();
            thumbnailTimers.Clear();
            foreach (var window in detachedSessionWindows.Values.ToArray()) window.Close();
            foreach (var session in viewModel.Sessions.ToArray()) session.Dispose();
        };
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (sessionCleanupCompleted || viewModel.Sessions.Count == 0) return;
        e.Cancel = true;
        if (sessionCleanupInProgress) return;
        sessionCleanupInProgress = true;
        viewModel.StatusMessage = "Closing remote sessions safely...";
        try
        {
            foreach (var session in viewModel.Sessions.ToArray())
            {
                CloseDetachedWindow(session);
                StopThumbnailUpdates(session);
                await viewModel.RemoveSessionAsync(session);
            }
            sessionCleanupCompleted = true;
            Close();
        }
        catch (Exception error) when (IsRecoverableWindowShutdownFailure(error))
        {
            sessionCleanupCompleted = true;
            viewModel.StatusMessage = "Remote sessions were released, but Windows could not complete every graceful window-close callback.";
            System.Windows.Application.Current.Shutdown(0);
        }
        finally
        {
            sessionCleanupInProgress = false;
        }
    }

    private static bool IsRecoverableWindowShutdownFailure(Exception error) => error is not
        (OutOfMemoryException or AccessViolationException);

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        UpdateToolbarOverflow(ActualWidth);
        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            viewModel.StatusMessage = "Local inventory could not be loaded.";
            MessageBox.Show(this,
                $"NextRDP could not open the local inventory. The database was not modified.\n\n{ex.Message}",
                "Inventory unavailable", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateToolbarOverflow(e.NewSize.Width);

    private void UpdateToolbarOverflow(double windowWidth)
    {
        if (ToolbarDiagnosticsButton is null || ToolbarCommandsButton is null || ToolbarOverflowButton is null) return;
        var useOverflow = windowWidth < 1050;
        var primaryVisibility = useOverflow ? Visibility.Collapsed : Visibility.Visible;
        ToolbarDiagnosticsButton.Visibility = primaryVisibility;
        ToolbarCommandsButton.Visibility = primaryVisibility;
        ToolbarOverflowButton.Visibility = useOverflow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToolbarOverflow_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = ToolbarOverflowButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        var diagnostics = new System.Windows.Controls.MenuItem
        {
            Header = "_Diagnostics…",
            IsEnabled = viewModel.HasSelectedConnection
        };
        diagnostics.Click += Diagnostics_Click;
        var commands = new System.Windows.Controls.MenuItem
        {
            Header = "_Commands…",
            InputGestureText = "Ctrl+Shift+P"
        };
        commands.Click += Commands_Click;
        menu.Items.Add(diagnostics);
        menu.Items.Add(commands);
        menu.IsOpen = true;
    }

    private async void NewConnection_Click(object sender, RoutedEventArgs e) => await ShowConnectionEditorAsync(null);

    private async void ConnectSelected_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedConnection is { } selected) await ConnectAsync(selected);
    }

    private void FocusSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async void ManagedSignIn_Click(object sender, RoutedEventArgs e)
    {
        try { await viewModel.SignInManagedAsync(); }
        catch (Exception ex) when (IsRecoverableUserOperationFailure(ex) || ex is HttpRequestException or TimeoutException)
        {
            ShowOperationFailure(ex, "Managed sign-in could not complete");
        }
    }

    private async void ManagedOrganization_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not System.Windows.Controls.ComboBox { SelectedItem: OrganizationDto organization }) return;
        try { await viewModel.SelectManagedOrganizationAsync(organization); }
        catch (Exception ex) when (IsRecoverableUserOperationFailure(ex) || ex is HttpRequestException) { ShowOperationFailure(ex, "Managed organization could not load"); }
    }

    private async void ManagedWorkspace_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not System.Windows.Controls.ComboBox { SelectedItem: WorkspaceDto workspace }) return;
        try { await viewModel.SelectManagedWorkspaceAsync(workspace); }
        catch (Exception ex) when (IsRecoverableUserOperationFailure(ex) || ex is HttpRequestException) { ShowOperationFailure(ex, "Managed workspace could not synchronize"); }
    }

    private async void ManagedSync_Click(object sender, RoutedEventArgs e)
    {
        try { await viewModel.SynchronizeManagedAsync(); }
        catch (Exception ex) when (IsRecoverableUserOperationFailure(ex) || ex is HttpRequestException) { ShowOperationFailure(ex, "Managed workspace could not synchronize"); }
    }

    private async void ManagedSignOut_Click(object sender, RoutedEventArgs e) => await viewModel.SignOutManagedAsync();

    private void ManagedTools_Click(object sender, RoutedEventArgs e) => new ManagedToolsWindow(viewModel, viewModel.SelectedConnection) { Owner = this }.ShowDialog();

    private async void TogglePinFolder_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedFolderNavigation is not { Kind: FolderNavigationKind.Folder, FolderId: Guid folderId })
        {
            MessageBox.Show(this, "Select a managed folder first.", "Pinned folders", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { await viewModel.TogglePinnedFolderAsync(folderId); }
        catch (Exception exception) when (IsRecoverableUserOperationFailure(exception)) { ShowOperationFailure(exception, "Pinned folder could not be updated"); }
    }

    private async void BulkConnect_Click(object sender, RoutedEventArgs e) => await BulkConnectSelectedAsync();

    private async Task BulkConnectSelectedAsync()
    {
        var selected = ConnectionList.SelectedItems.Cast<ConnectionProfile>().Where(viewModel.IsManagedConnection).DistinctBy(profile => profile.Id).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "Select one or more managed connections first. Use Ctrl or Shift to select several rows.", "Bulk connect", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var preview = await viewModel.PreviewBulkConnectAsync(selected.Select(profile => profile.Id).ToArray());
            var message = $"Allowed sessions: {preview.AllowedConnectionIds.Count}\nDenied or over limit: {preview.DeniedConnectionIds.Count}\nHard limit: {preview.MaximumConnections}\n\n{string.Join("\n", preview.Warnings)}\n\nEach session opens its own Windows credential prompt. Continue?";
            if (MessageBox.Show(this, message, "Confirm bulk connect", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            foreach (var profile in selected.Where(profile => preview.AllowedConnectionIds.Contains(profile.Id))) await ConnectAsync(profile);
        }
        catch (Exception exception) when (IsRecoverableUserOperationFailure(exception) || exception is HttpRequestException)
        {
            ShowOperationFailure(exception, "Bulk connect could not start");
        }
    }

    private async void ReopenWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var profiles = viewModel.WorkspaceDescriptors.Select(descriptor => descriptor.Connection).ToArray();
        if (profiles.Length == 0) return;
        ConnectionList.SelectedItems.Clear();
        foreach (var profile in profiles)
            if (ConnectionList.Items.Contains(profile)) ConnectionList.SelectedItems.Add(profile);
        await BulkConnectSelectedAsync();
    }

    private void Commands_Click(object sender, RoutedEventArgs e) => OpenCommandPalette();

    private void OpenCommandPalette()
    {
        var commands = new List<CommandPaletteItem>
        {
            new("Focus enterprise search", "Ctrl+K", () => { SearchBox.Focus(); SearchBox.SelectAll(); return Task.CompletedTask; }),
            new("Create local connection", "Ctrl+N", () => ShowConnectionEditorAsync(null)),
            new("Synchronize managed workspace", string.Empty, async () => await viewModel.SynchronizeManagedAsync()),
            new("Open managed workspace tools", string.Empty, () => { new ManagedToolsWindow(viewModel, viewModel.SelectedConnection) { Owner = this }.ShowDialog(); return Task.CompletedTask; })
        };
        if (viewModel.SelectedConnection is { } selected && viewModel.CanLaunch(selected))
            commands.Add(new CommandPaletteItem($"Connect: {selected.Name}", "Enter", () => ConnectAsync(selected)));
        var selectedManaged = ConnectionList.SelectedItems.Cast<ConnectionProfile>().Where(viewModel.IsManagedConnection).ToArray();
        if (selectedManaged.Length > 0)
        {
            commands.Add(new CommandPaletteItem($"Bulk connect {selectedManaged.Length} selected", string.Empty, BulkConnectSelectedAsync));
            commands.Add(new CommandPaletteItem("Save selected as managed workspace", string.Empty, async () =>
            {
                var prompt = new TextPromptWindow("Save managed workspace", "Workspace name:", "My workspace") { Owner = this };
                if (prompt.ShowDialog() == true) await viewModel.SaveWorkspaceLayoutAsync(prompt.Value, selectedManaged.Select(profile => profile.Id).ToArray(), NextRdp.Platform.Domain.SessionDisplayMode.Embedded);
            }));
        }
        new CommandPaletteWindow(commands) { Owner = this }.ShowDialog();
    }

    private async void EditConnection_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedConnection is null)
        {
            MessageBox.Show(this, "Select a connection to edit.", "Edit connection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await ShowConnectionEditorAsync(viewModel.SelectedConnection);
    }

    private async Task ShowConnectionEditorAsync(ConnectionProfile? profile)
    {
        if (profile is not null && viewModel.IsManagedConnection(profile))
        {
            new ManagedToolsWindow(viewModel, profile) { Owner = this }.ShowDialog();
            return;
        }
        var dialog = new ConnectionEditorWindow(viewModel.GetFolderChoices(), profile) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        try { await viewModel.SaveConnectionAsync(dialog.Result); }
        catch (Exception ex) when (IsRecoverableUserOperationFailure(ex)) { ShowOperationFailure(ex, "Cannot save connection"); }
    }

    private async void DeleteConnection_Click(object sender, RoutedEventArgs e)
    {
        var profile = viewModel.SelectedConnection;
        if (profile is null) return;
        if (MessageBox.Show(this, $"Delete ‘{profile.Name}’? This removes connection metadata only.", "Delete connection", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await viewModel.DeleteConnectionAsync(profile); }
        catch (Exception ex) when (IsRecoverableUserOperationFailure(ex)) { ShowOperationFailure(ex, "Cannot delete connection"); }
    }

    private async void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedConnection is not { } profile) return;
        try { await viewModel.ToggleFavoriteAsync(profile); }
        catch (Exception ex) when (IsRecoverableUserOperationFailure(ex)) { ShowOperationFailure(ex, "Cannot update favorite"); }
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var defaultParent = viewModel.SelectedFolderNavigation?.Kind == FolderNavigationKind.Folder
            ? viewModel.SelectedFolderNavigation.FolderId
            : null;
        var choices = viewModel.GetFolderChoices();
        var dialog = new FolderEditorWindow(choices) { Owner = this };
        if (defaultParent is { } parentId) dialog.ParentBox.SelectedItem = choices.FirstOrDefault(x => x.Id == parentId);
        if (dialog.ShowDialog() != true) return;
        try { await viewModel.SaveFolderAsync(dialog.FolderName, dialog.ParentId); }
        catch (Exception ex) when (IsRecoverableUserOperationFailure(ex)) { ShowOperationFailure(ex, "Cannot save folder"); }
    }

    private async void EditFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = GetSelectedFolder();
        if (folder is null)
        {
            MessageBox.Show(this, "Select a connection folder to edit. All, Favorites, and Recent are built-in views.", "Edit folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new FolderEditorWindow(viewModel.GetFolderChoices(folder.Id), folder) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try { await viewModel.SaveFolderAsync(dialog.FolderName, dialog.ParentId, folder.Id); }
        catch (Exception ex) when (IsRecoverableUserOperationFailure(ex)) { ShowOperationFailure(ex, "Cannot save folder"); }
    }

    private async void DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = GetSelectedFolder();
        if (folder is null) return;
        if (MessageBox.Show(this, $"Delete the empty folder ‘{folder.Name}’?", "Delete folder", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await viewModel.DeleteFolderAsync(folder); }
        catch (Exception ex) when (IsRecoverableUserOperationFailure(ex)) { ShowOperationFailure(ex, "Cannot delete folder"); }
    }

    private ConnectionFolder? GetSelectedFolder() =>
        viewModel.SelectedFolderNavigation is { Kind: FolderNavigationKind.Folder, FolderId: { } id }
            ? viewModel.Folders.FirstOrDefault(x => x.Id == id && !viewModel.IsManagedFolder(x.Id))
            : null;

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        viewModel.SelectedFolderNavigation = e.NewValue as FolderNavigationNode;

    private async void QuickConnect_Click(object sender, RoutedEventArgs e)
    {
        try { await ConnectAsync(await viewModel.SaveQuickConnectionAsync()); }
        catch (Exception ex) when (IsRecoverableUserOperationFailure(ex)) { ShowOperationFailure(ex, "Cannot connect"); }
    }

    private async void Connection_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (viewModel.SelectedConnection is not null) await ConnectAsync(viewModel.SelectedConnection);
    }

    private void ConnectionItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBoxItem item) return;
        if (!item.IsSelected)
        {
            ConnectionList.SelectedItems.Clear();
            item.IsSelected = true;
        }
        item.Focus();
    }

    private void ConnectionItem_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBoxItem { DataContext: ConnectionProfile profile } item)
        {
            e.Handled = true;
            return;
        }
        if (!item.IsSelected)
        {
            ConnectionList.SelectedItems.Clear();
            item.IsSelected = true;
        }
        item.Focus();

        var menu = item.ContextMenu ?? new System.Windows.Controls.ContextMenu();
        menu.Items.Clear();
        var connect = new System.Windows.Controls.MenuItem
        {
            Header = "_Connect",
            InputGestureText = "Enter",
            IsEnabled = viewModel.CanLaunch(profile)
        };
        connect.Click += ConnectSelected_Click;
        var edit = new System.Windows.Controls.MenuItem
        {
            Header = viewModel.IsManagedConnection(profile) ? "Managed _details…" : "_Edit…",
            InputGestureText = "Ctrl+E"
        };
        edit.Click += EditConnection_Click;
        var diagnostics = new System.Windows.Controls.MenuItem { Header = "_Diagnostics…" };
        diagnostics.Click += Diagnostics_Click;
        menu.Items.Add(connect);
        menu.Items.Add(edit);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(diagnostics);
        item.ContextMenu = menu;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Remote Desktop files (*.rdp;*.rdg)|*.rdp;*.rdg", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var preview = await viewModel.PreviewImportAsync(dialog.FileName);
            if (preview.Errors.Count > 0)
            {
                MessageBox.Show(this, string.Join(Environment.NewLine, preview.Errors), "Import rejected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var previewDialog = new ImportPreviewWindow(preview, viewModel.IsDuplicate) { Owner = this };
            if (previewDialog.ShowDialog() != true) return;
            var result = await viewModel.CommitImportAsync(previewDialog.SelectedCandidates);
            MessageBox.Show(this, $"Imported {result.Imported} connection(s). Skipped {result.Skipped} duplicate or invalid item(s). Sensitive fields were removed before preview.", "Import complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (IsRecoverableUserOperationFailure(ex)) { ShowOperationFailure(ex, "Import failed", MessageBoxImage.Error); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
            "The export contains connection names, hosts, ports, folder names, tags, username hints, and notes. It contains no passwords or credential references, but the metadata can still be sensitive. Continue?",
            "Export inventory metadata", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var dialog = new SaveFileDialog { Filter = "NextRDP inventory (*.json)|*.json", FileName = $"NextRDP-inventory-{DateTime.Now:yyyyMMdd}.json", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await viewModel.ExportInventoryAsync(dialog.FileName);
            MessageBox.Show(this, "Inventory exported without passwords, password blobs, tokens, or credential references.", "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (IsRecoverableUserOperationFailure(ex)) { ShowOperationFailure(ex, "Export failed", MessageBoxImage.Error); }
    }

    private async void SupportPackage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "ZIP archive (*.zip)|*.zip", FileName = $"NextRDP-support-{DateTime.Now:yyyyMMdd-HHmm}.zip", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await viewModel.CreateSupportPackageAsync(dialog.FileName);
            MessageBox.Show(this, "Created a redacted package without hostnames, connection names, usernames, notes, folder names, or credentials.", "Support package", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (IsRecoverableUserOperationFailure(ex)) { ShowOperationFailure(ex, "Support package failed", MessageBoxImage.Error); }
    }

    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedConnection is null)
        {
            MessageBox.Show(this, "Select a connection first.", "Diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var profile = viewModel.SelectedConnection;
            var result = await viewModel.DiagnoseAsync(profile);
            var nla = profile.Settings.RequireNetworkLevelAuthentication ? "Required" : "Not required";
            var attemptDetails = result.Attempts.Count == 0
                ? "No TCP attempts were made."
                : string.Join("\n", result.Attempts.Select(attempt =>
                    $"- {attempt.Address} ({attempt.AddressFamily}): {attempt.Outcome} in {attempt.Duration.TotalMilliseconds:N0} ms"));
            string rdpCapability;
            try
            {
                var capability = RdpSessionHostControl.ProbeCapability();
                rdpCapability = $"{capability.ControlName}; client interface: {(capability.SupportsRequiredClientInterface ? "available" : "missing")}; advanced settings: {(capability.SupportsAdvancedSettings ? "available" : "missing")}; prompt-only credentials: {(capability.SupportsPromptOnlyCredentials ? "available" : "missing")}";
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or COMException or InvalidOperationException)
            {
                rdpCapability = $"Unavailable: {ex.Message}";
            }
            MessageBox.Show(this,
                $"{result.Message}\n\nResolved addresses: {(result.Addresses.Count == 0 ? "None" : string.Join(", ", result.Addresses))}\nPort: {result.Port}\nNLA policy: {nla}\nMicrosoft RDP control: {rdpCapability}\nTotal duration: {result.Duration.TotalMilliseconds:N0} ms\n\nTCP attempts:\n{attemptDetails}",
                "Connectivity diagnostics", MessageBoxButton.OK, result.PortReachable ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) when (IsRecoverableUserOperationFailure(ex)) { ShowOperationFailure(ex, "Diagnostics failed", MessageBoxImage.Error); }
    }

    private void Theme_Click(object sender, RoutedEventArgs e) => ((App)System.Windows.Application.Current).ToggleTheme();

    private void Help_Click(object sender, RoutedEventArgs e) => new HelpWindow { Owner = this }.ShowDialog();

    private async Task ConnectAsync(ConnectionProfile profile)
    {
        if (!viewModel.CanLaunch(profile))
        {
            MessageBox.Show(this, viewModel.LaunchRestriction(profile), "Managed connection is read-only", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var decision = await viewModel.AuthorizeAsync(profile);
        if (!decision.Allowed)
        {
            MessageBox.Show(this, decision.Reason, "Policy denied connection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (profile.Environment == ConnectionEnvironment.Production &&
            MessageBox.Show(this, $"You are connecting to PRODUCTION: {profile.Name}. Continue?", "Production connection", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var sessionId = Guid.CreateVersion7();
        try
        {
            await viewModel.AuditAsync(new AuditEvent
            {
                Type = AuditEventType.ConnectionAttempted,
                ConnectionId = profile.Id,
                SessionId = sessionId,
                Outcome = "attempted",
                RedactedDetail = $"port={profile.Port} environment={profile.Environment}"
            });
        }
        catch (Exception ex) when (IsRecoverableLocalDataFailure(ex))
        {
            viewModel.StatusMessage = "The connection was not started because its required local audit entry could not be recorded.";
            MessageBox.Show(this, viewModel.StatusMessage, "Local audit unavailable", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        RdpSessionHostControl? control = null;
        System.Windows.Forms.Integration.WindowsFormsHost? host = null;
        SessionTab? tab = null;
        var failureStateObserved = false;
        try
        {
            control = new RdpSessionHostControl();
            host = new System.Windows.Forms.Integration.WindowsFormsHost { Child = control };
            var detached = profile.Settings.DisplayMode is RdpDisplayMode.SeparateWindow or RdpDisplayMode.SelectedMonitors;
            tab = new SessionTab(profile.Name, profile.Id, profile.Environment,
                profile.Environment == ConnectionEnvironment.Production && profile.Settings.ShowProductionWatermark,
                host, control, profile.Settings.ThumbnailMode, detached);
            viewModel.AddSession(tab);
            if (detached) OpenDetachedWindow(tab, profile.Settings.DisplayMode, profile.Settings.SelectedMonitorIds);
            await Dispatcher.InvokeAsync(() =>
            {
                host.UpdateLayout();
                var dpi = VisualTreeHelper.GetDpi(host);
                var desktopSize = ResolveInitialDesktopSize(
                    host.ActualWidth,
                    host.ActualHeight,
                    SessionTabs.ActualWidth,
                    SessionTabs.ActualHeight,
                    dpi.DpiScaleX,
                    dpi.DpiScaleY);
                control.SetDesktopSize(desktopSize.Width, desktopSize.Height);
            }, DispatcherPriority.Loaded);

            control.StateChanged += (_, state) =>
            {
                if (state == SessionState.Failed) failureStateObserved = true;
                _ = tab.StateChanges.Enqueue(() => HandleSessionStateChangedAsync(tab, profile, sessionId, state));
            };
            control.DiagnosticMessage += (_, message) => Dispatcher.Invoke(() => viewModel.StatusMessage = message);
            control.DiagnosticRaised += (_, diagnostic) =>
            {
                if (diagnostic.IsFailure)
                    _ = tab.StateChanges.Enqueue(() => RecordSessionDiagnosticAsync(profile, sessionId, diagnostic));
            };
            await control.ConnectAsync(profile);
            if (profile.Settings.DisplayMode is RdpDisplayMode.FullScreen or RdpDisplayMode.AllMonitors) control.SetFullScreen(true);
        }
        catch (Exception ex) when (IsRdpStartupFailure(ex))
        {
            if (!failureStateObserved)
            {
                try
                {
                    await viewModel.AuditAsync(new AuditEvent { Type = AuditEventType.ConnectionFailed, ConnectionId = profile.Id, SessionId = sessionId, Outcome = "startup-failed", RedactedDetail = $"error_type={ex.GetType().Name}" });
                }
                catch (Exception auditError) when (IsRecoverableLocalDataFailure(auditError))
                {
                    viewModel.StatusMessage = "The RDP session failed to start and its local failure audit could not be recorded.";
                }
            }
            if (tab is not null && viewModel.Sessions.Contains(tab))
            {
                CloseDetachedWindow(tab);
                await viewModel.RemoveSessionAsync(tab);
            }
            else
            {
                control?.Dispose();
                host?.Dispose();
            }
            viewModel.StatusMessage = "The RDP session could not start. Review the displayed diagnostic and local RDP client installation.";
            MessageBox.Show(this, ex.Message, "RDP connection could not start", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsRdpStartupFailure(Exception error) => error is
        PlatformNotSupportedException or
        COMException or
        InvalidOperationException or
        InvalidCastException or
        MissingMemberException or
        TypeInitializationException or
        System.ComponentModel.LicenseException or
        NotSupportedException;

    internal static (int Width, int Height) ResolveInitialDesktopSize(
        double hostWidth,
        double hostHeight,
        double sessionAreaWidth,
        double sessionAreaHeight,
        double dpiScaleX,
        double dpiScaleY)
    {
        var widthDip = hostWidth >= 1 ? hostWidth : Math.Max(640 / Math.Max(dpiScaleX, 1), sessionAreaWidth - 6);
        var heightDip = hostHeight >= 1 ? hostHeight : Math.Max(480 / Math.Max(dpiScaleY, 1), sessionAreaHeight - 56);
        return (
            Math.Clamp((int)Math.Round(widthDip * Math.Max(dpiScaleX, 1)), 640, 8192),
            Math.Clamp((int)Math.Round(heightDip * Math.Max(dpiScaleY, 1)), 480, 4320));
    }

    private static bool IsRecoverableLocalDataFailure(Exception error) => error is
        DbException or
        IOException or
        UnauthorizedAccessException or
        InvalidOperationException;

    internal static bool IsRecoverableUserOperationFailure(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is ArgumentException or XmlException or DbException or IOException or UnauthorizedAccessException or InvalidOperationException)
                return true;
        return false;
    }

    private void ShowOperationFailure(Exception error, string title, MessageBoxImage image = MessageBoxImage.Warning)
    {
        var message = GetRecoverableUserMessage(error);
        viewModel.StatusMessage = message;
        MessageBox.Show(this, message, title, MessageBoxButton.OK, image);
    }

    internal static string GetRecoverableUserMessage(Exception error)
    {
        var chain = new List<Exception>();
        for (Exception? current = error; current is not null; current = current.InnerException) chain.Add(current);
        if (chain.Any(current => current is DbException))
            return "The local inventory database could not complete the operation. Restart NextRDP and create a redacted support package if the problem repeats.";
        if (chain.Any(current => current is UnauthorizedAccessException))
            return "NextRDP could not access the selected file or local data. Verify that your account has permission and try again.";
        if (chain.Any(current => current is IOException))
            return "The selected file or local data could not be read or written. Close other programs using it, verify available disk space, and try again.";
        if (error is ArgumentException or XmlException or InvalidOperationException) return error.Message;
        return "The operation could not be completed because local application data was unavailable. No connection password was stored.";
    }

    private async Task HandleSessionStateChangedAsync(SessionTab tab, ConnectionProfile profile, Guid sessionId, SessionState state)
    {
        try
        {
            if (!Dispatcher.HasShutdownStarted)
                await Dispatcher.InvokeAsync(() => tab.Status = state.ToString());
            if (state == SessionState.Connected)
            {
                if (!Dispatcher.HasShutdownStarted)
                    await Dispatcher.InvokeAsync(() => StartThumbnailUpdates(tab));
                await viewModel.MarkConnectedAsync(profile.Id);
            }
            else if (!Dispatcher.HasShutdownStarted)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    StopThumbnailUpdates(tab);
                    tab.ClearThumbnail(state is SessionState.Connecting or SessionState.Reconnecting ? "Hidden during authentication" : "No live preview");
                });
            }
            if (state is SessionState.Connected or SessionState.Failed or SessionState.Disconnected)
                await viewModel.AuditAsync(new AuditEvent
                {
                    Type = state == SessionState.Connected ? AuditEventType.ConnectionSucceeded : state == SessionState.Failed ? AuditEventType.ConnectionFailed : AuditEventType.SessionDisconnected,
                    ConnectionId = profile.Id,
                    SessionId = sessionId,
                    Outcome = state.ToString()
                });
        }
        catch (Exception)
        {
            if (!Dispatcher.HasShutdownStarted)
                await Dispatcher.InvokeAsync(() => viewModel.StatusMessage = "The session state changed, but its local audit update could not be completed.");
        }
    }

    private async Task RecordSessionDiagnosticAsync(ConnectionProfile profile, Guid sessionId, RdpSessionDiagnostic diagnostic)
    {
        try
        {
            await viewModel.AuditAsync(new AuditEvent
            {
                Type = AuditEventType.ConnectionFailed,
                ConnectionId = profile.Id,
                SessionId = sessionId,
                Outcome = diagnostic.Category,
                RedactedDetail = $"rdp_code=0x{unchecked((uint)diagnostic.Code):X8}"
            });
        }
        catch (Exception)
        {
            if (!Dispatcher.HasShutdownStarted)
                await Dispatcher.InvokeAsync(() => viewModel.StatusMessage = "The RDP failure was shown, but its redacted audit category could not be recorded.");
        }
    }

    private async void CloseSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionTab tab })
        {
            CloseDetachedWindow(tab);
            StopThumbnailUpdates(tab);
            await viewModel.RemoveSessionAsync(tab);
        }
    }

    private void OpenDetachedWindow(SessionTab tab, RdpDisplayMode mode, IReadOnlyList<uint> selectedMonitorIds)
    {
        var window = new Window
        {
            Title = $"NextRDP - {tab.Name}",
            Icon = Icon,
            MinWidth = 640,
            MinHeight = 480,
            Width = 1280,
            Height = 800,
            Content = tab.HostView,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        if (mode == RdpDisplayMode.SelectedMonitors)
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            var selected = selectedMonitorIds.Where(id => id < screens.Length).Select(id => screens[(int)id]).ToArray();
            if (selected.Length == 0) throw new NotSupportedException("None of the centrally selected monitors is available on this computer.");
            var left = selected.Min(screen => screen.Bounds.Left);
            var top = selected.Min(screen => screen.Bounds.Top);
            var right = selected.Max(screen => screen.Bounds.Right);
            var bottom = selected.Max(screen => screen.Bounds.Bottom);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left;
            window.Top = top;
            window.Width = right - left;
            window.Height = bottom - top;
            window.WindowStyle = WindowStyle.None;
            window.WindowState = WindowState.Normal;
        }
        window.Closed += (_, _) =>
        {
            detachedSessionWindows.Remove(tab.SessionId);
            window.Content = null;
            if (!detachedWindowTeardown.Remove(tab.SessionId) && viewModel.Sessions.Contains(tab))
            {
                tab.Reattach();
                viewModel.SelectedSession = tab;
                Activate();
            }
        };
        detachedSessionWindows[tab.SessionId] = window;
        window.Show();
    }

    private void CloseDetachedWindow(SessionTab tab)
    {
        if (!detachedSessionWindows.TryGetValue(tab.SessionId, out var window)) return;
        detachedWindowTeardown.Add(tab.SessionId);
        window.Close();
    }

    private void StartThumbnailUpdates(SessionTab tab)
    {
        if (!tab.IsThumbnailEnabled || thumbnailTimers.ContainsKey(tab.SessionId)) return;
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        timer.Tick += (_, _) => RefreshThumbnail(tab);
        thumbnailTimers[tab.SessionId] = timer;
        tab.ClearThumbnail("Preparing privacy-safe preview");
        timer.Start();
        RefreshThumbnail(tab);
    }

    private void StopThumbnailUpdates(SessionTab tab)
    {
        if (!thumbnailTimers.Remove(tab.SessionId, out var timer)) return;
        timer.Stop();
    }

    private static void RefreshThumbnail(SessionTab tab)
    {
        if (!tab.IsThumbnailEnabled || tab.SessionHost.State != SessionState.Connected || tab.SessionHost is not RdpSessionHostControl control) return;
        var png = control.CaptureThumbnailPng();
        if (png is null) return;
        using var stream = new MemoryStream(png, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        tab.SetThumbnail(image);
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedSession?.SessionHost is RdpSessionHostControl control) control.SetFullScreen(true);
    }

    private void SecureAttention_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedSession?.SessionHost is not RdpSessionHostControl control)
        {
            viewModel.StatusMessage = "Select an active RDP session before sending Ctrl+Alt+End.";
            return;
        }

        if (control.SendSecureAttentionSequence())
            viewModel.StatusMessage = "Sent Ctrl+Alt+End to the remote session.";
    }

    private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.P && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; OpenCommandPalette(); return; }
        if (e.Key == Key.K && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if (e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; await ShowConnectionEditorAsync(null); return; }
        if (e.Key == Key.E && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && viewModel.SelectedConnection is not null) { e.Handled = true; await ShowConnectionEditorAsync(viewModel.SelectedConnection); return; }
        if (e.Key == Key.D && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && viewModel.SelectedConnection is { } favorite && !viewModel.IsManagedConnection(favorite)) { e.Handled = true; await viewModel.ToggleFavoriteAsync(favorite); return; }
        if (e.Key == Key.Enter && ConnectionList.IsKeyboardFocusWithin && viewModel.SelectedConnection is { } selected) { e.Handled = true; await ConnectAsync(selected); }
    }
}
