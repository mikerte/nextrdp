using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows.Forms.Integration;
using NextRdp.Core;
using NextRdp.Desktop;
using NextRdp.Platform.Application;
using NextRdp.Platform.Client;

namespace NextRdp.Desktop.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public void UnhandledExceptionMarkerExcludesExceptionMessagesAndSensitiveValues()
    {
        var error = new InvalidOperationException(
            "credential-sentinel-must-not-be-recorded",
            new DiagnosticTestException("target-sentinel-must-not-be-recorded", unchecked((int)0x80004005)));

        var json = JsonSerializer.Serialize(App.CreateUnhandledExceptionSummary(error));

        Assert.DoesNotContain("credential-sentinel", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("target-sentinel", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(typeof(InvalidOperationException).FullName!, json, StringComparison.Ordinal);
        Assert.Contains(nameof(DiagnosticTestException), json, StringComparison.Ordinal);
        Assert.Contains(unchecked((int)0x80004005).ToString(CultureInfo.InvariantCulture), json, StringComparison.Ordinal);
    }

    private sealed class DiagnosticTestException : Exception
    {
        public DiagnosticTestException(string message, int hResult) : base(message)
        {
            HResult = hResult;
        }
    }

    [Fact]
    public void PrivacyLockedAndDisabledSessionsNeverEnableThumbnails()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var locked = new SessionTab("Locked", Guid.NewGuid(), ConnectionEnvironment.Production, true,
                    new WindowsFormsHost(), new StubSessionHost(), RdpThumbnailMode.PrivacyLocked);
                using var disabled = new SessionTab("Disabled", Guid.NewGuid(), ConnectionEnvironment.Development, false,
                    new WindowsFormsHost(), new StubSessionHost(), RdpThumbnailMode.Disabled);

                Assert.False(locked.IsThumbnailEnabled);
                Assert.False(disabled.IsThumbnailEnabled);
                locked.ClearThumbnail("should remain hidden");
                Assert.Null(locked.Thumbnail);
                Assert.Empty(locked.ThumbnailStatus);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "RDP thumbnail policy test timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public async Task SessionStateWorkRunsInArrivalOrder()
    {
        var queue = new OrderedAsyncQueue();
        var firstMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new List<string>();

        _ = queue.Enqueue(async () =>
        {
            observed.Add("connected-start");
            firstStarted.SetResult();
            await firstMayFinish.Task;
            observed.Add("connected-finish");
        });
        await firstStarted.Task;
        _ = queue.Enqueue(() =>
        {
            observed.Add("disconnected");
            return Task.CompletedTask;
        });

        Assert.Equal(["connected-start"], observed);
        firstMayFinish.SetResult();
        await queue.WhenIdleAsync();
        Assert.Equal(["connected-start", "connected-finish", "disconnected"], observed);
    }

    [Fact]
    public void ClosingOneOfTwoSessionTabsLeavesTheOtherSessionRunning()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var viewModel = Create(new MemoryRepository());
                var firstHost = new StubSessionHost();
                var secondHost = new StubSessionHost();
                var first = new SessionTab("First", Guid.NewGuid(), ConnectionEnvironment.Development, false, new WindowsFormsHost(), firstHost);
                var second = new SessionTab("Second", Guid.NewGuid(), ConnectionEnvironment.Test, false, new WindowsFormsHost(), secondHost);

                viewModel.AddSession(first);
                viewModel.AddSession(second);

                Assert.Equal(2, viewModel.Sessions.Count);
                Assert.Same(second, viewModel.SelectedSession);
                viewModel.RemoveSessionAsync(first).GetAwaiter().GetResult();

                Assert.Same(second, Assert.Single(viewModel.Sessions));
                Assert.Same(second, viewModel.SelectedSession);
                Assert.True(firstHost.DisconnectCalled);
                Assert.True(firstHost.Disposed);
                Assert.False(secondHost.DisconnectCalled);
                Assert.False(secondHost.Disposed);

                viewModel.RemoveSessionAsync(second).GetAwaiter().GetResult();
                Assert.Empty(viewModel.Sessions);
                Assert.Null(viewModel.SelectedSession);
            }
            catch (Exception ex) { failure = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Multiple-session lifecycle test timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public void SessionCleanupContainsQueuedStateAndControlDisposalFailures()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var viewModel = Create(new MemoryRepository());
                var sessionHost = new StubSessionHost { DisposeFailure = new InvalidOperationException("simulated local control cleanup failure") };
                var tab = new SessionTab("Cleanup", Guid.NewGuid(), ConnectionEnvironment.Test, false, new WindowsFormsHost(), sessionHost);
                viewModel.AddSession(tab);
                _ = tab.StateChanges.Enqueue(() => Task.FromException(new InvalidOperationException("simulated queued state failure")));

                viewModel.RemoveSessionAsync(tab).GetAwaiter().GetResult();

                Assert.Empty(viewModel.Sessions);
                Assert.Null(viewModel.SelectedSession);
                Assert.True(sessionHost.DisconnectCalled);
                Assert.True(sessionHost.Disposed);
                Assert.Contains("session closed", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) { failure = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Recoverable session cleanup test timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public void DetachedSessionCanReturnToItsOriginalTabWithoutReplacingTheHost()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var hostView = new WindowsFormsHost();
                using var tab = new SessionTab("Detached", Guid.NewGuid(), ConnectionEnvironment.Test, false, hostView, new StubSessionHost());

                tab.Detach();
                Assert.True(tab.IsDetached);
                Assert.NotSame(hostView, tab.SessionContent);

                tab.Reattach();
                Assert.False(tab.IsDetached);
                Assert.Same(hostView, tab.SessionContent);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Detached session lifecycle test timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public void RecoverableUserOperationFailuresIncludeNestedLocalDataErrors()
    {
        Assert.True(MainWindow.IsRecoverableUserOperationFailure(new InvalidOperationException("invalid local state")));
        Assert.True(MainWindow.IsRecoverableUserOperationFailure(new NotSupportedException("wrapper", new System.IO.IOException("disk unavailable"))));
        Assert.False(MainWindow.IsRecoverableUserOperationFailure(new NotSupportedException("unexpected programming error")));
        Assert.DoesNotContain("private\\customer.rdp", MainWindow.GetRecoverableUserMessage(
            new IOException("Cannot open C:\\private\\customer.rdp")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission", MainWindow.GetRecoverableUserMessage(
            new UnauthorizedAccessException("Access denied to C:\\private\\customer.rdp")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InitialRdpDesktopSizePrefersArrangedHostAndFallsBackToSessionArea()
    {
        Assert.Equal((1800, 1125), MainWindow.ResolveInitialDesktopSize(1200, 750, 1600, 900, 1.5, 1.5));
        Assert.Equal((1594, 844), MainWindow.ResolveInitialDesktopSize(0, 0, 1600, 900, 1, 1));
        Assert.Equal((640, 480), MainWindow.ResolveInitialDesktopSize(0, 0, 0, 0, 1, 1));
    }

    [Fact]
    public void AppDataDirectoryUsesTheProductFolderUnlessAnAbsoluteTestOverrideIsProvided()
    {
        var local = Path.Combine(Path.GetTempPath(), "local-app-data");
        var isolated = Path.Combine(Path.GetTempPath(), "nextrdp-isolated-smoke");

        Assert.Equal(Path.Combine(local, "NextRDP"), AppDataDirectoryResolver.Resolve(local, null));
        Assert.Equal(Path.GetFullPath(isolated), AppDataDirectoryResolver.Resolve(local, isolated));
        Assert.Throws<InvalidOperationException>(() => AppDataDirectoryResolver.Resolve(local, "relative-smoke-data"));
    }

    [Fact]
    public async Task NavigationIncludesNestedFoldersFavoritesAndRecentConnections()
    {
        var repository = new MemoryRepository();
        var parent = new ConnectionFolder { Name = "Production" };
        var child = new ConnectionFolder { Name = "Database", ParentId = parent.Id };
        await repository.SaveFolderAsync(parent);
        await repository.SaveFolderAsync(child);
        await repository.SaveAsync(new ConnectionProfile { Name = "SQL", Host = "sql-01", FolderId = child.Id, IsFavorite = true, LastConnectedAtUtc = DateTimeOffset.UtcNow });
        await repository.SaveAsync(new ConnectionProfile { Name = "Web", Host = "web-01" });
        var viewModel = Create(repository);

        await viewModel.InitializeAsync();

        Assert.Equal(["All connections", "Favorites", "Recent", "Production"], viewModel.FolderNavigation.Select(x => x.Name));
        Assert.Equal("Database", Assert.Single(viewModel.FolderNavigation[3].Children).Name);
        viewModel.SelectedFolderNavigation = viewModel.FolderNavigation[1];
        Assert.Equal("SQL", Assert.Single(viewModel.FilteredConnections).Name);
        viewModel.SelectedFolderNavigation = viewModel.FolderNavigation[2];
        Assert.Equal("SQL", Assert.Single(viewModel.FilteredConnections).Name);
        viewModel.SelectedFolderNavigation = viewModel.FolderNavigation[3];
        Assert.Equal("SQL", Assert.Single(viewModel.FilteredConnections).Name);
    }

    [Fact]
    public async Task MarkConnectedIgnoresUnknownProfilesWithoutCreatingLocalInventory()
    {
        var repository = new MemoryRepository();
        var viewModel = Create(repository);
        await viewModel.InitializeAsync();

        await viewModel.MarkConnectedAsync(Guid.NewGuid());

        Assert.Empty(repository.Connections);
        Assert.Empty(viewModel.Connections);
    }

    [Fact]
    public async Task MarkConnectedUpdatesManagedRecencyWithoutCreatingLocalInventory()
    {
        var repository = new MemoryRepository();
        var managedClient = new StubManagedWorkspaceClient();
        var viewModel = new MainViewModel(
            repository,
            new StubImportService(),
            new MemoryAuditStore(),
            new StubDiagnostics(),
            new AllowPolicy(),
            new StubExportService(),
            managedClient);
        await viewModel.InitializeAsync();
        var managed = Assert.Single(viewModel.Connections);
        Assert.Null(managed.LastConnectedAtUtc);

        await viewModel.MarkConnectedAsync(managed.Id);

        Assert.NotNull(managed.LastConnectedAtUtc);
        Assert.Empty(repository.Connections);
        viewModel.SelectedFolderNavigation = viewModel.FolderNavigation.Single(item => item.Name == "Recent");
        Assert.Same(managed, Assert.Single(viewModel.FilteredConnections));

        await viewModel.SynchronizeManagedAsync();
        Assert.NotNull(Assert.Single(viewModel.Connections).LastConnectedAtUtc);
        Assert.Empty(repository.Connections);
    }

    [Fact]
    public async Task FuzzySearchAndFavoriteToggleUpdateTheVisibleInventory()
    {
        var repository = new MemoryRepository();
        var profile = new ConnectionProfile { Name = "Production SQL", Host = "PRD-SRV-01", Tags = ["database"] };
        await repository.SaveAsync(profile);
        var viewModel = Create(repository);
        await viewModel.InitializeAsync();
        viewModel.SearchText = "prdsrv";
        Assert.Single(viewModel.FilteredConnections);
        await viewModel.ToggleFavoriteAsync(viewModel.FilteredConnections[0]);
        Assert.True(Assert.Single(repository.Connections).IsFavorite);
    }

    [Fact]
    public async Task CommitImportSkipsExistingAndInvalidCandidates()
    {
        var repository = new MemoryRepository();
        await repository.SaveAsync(new ConnectionProfile { Name = "Existing", Host = "server-01" });
        var viewModel = Create(repository);
        await viewModel.InitializeAsync();
        var candidates = new[]
        {
            new ImportCandidate("test", "Duplicate", "server-01", 3389, null, [], new RdpSettings(), []),
            new ImportCandidate("test", "Canonical duplicate", "SERVER-01.", 3389, null, [], new RdpSettings(), []),
            new ImportCandidate("test", "New", "server-02", 3389, null, ["Imported"], new RdpSettings(), []),
            new ImportCandidate("test", "Invalid", "bad host", 3389, null, [], new RdpSettings(), ["invalid"])
        };
        var result = await viewModel.CommitImportAsync(candidates);
        Assert.Equal(1, result.Imported);
        Assert.Equal(3, result.Skipped);
        Assert.Equal(2, repository.Connections.Count);
        Assert.Single(repository.Folders);
    }

    [Fact]
    public async Task AuditMetadataDoesNotPersistTargetsAddressesOrSourceFileNames()
    {
        var repository = new MemoryRepository();
        var profile = new ConnectionProfile { Name = "Private target", Host = "sensitive-host.internal", Port = 3391 };
        await repository.SaveAsync(profile);
        var audit = new MemoryAuditStore();
        var viewModel = Create(repository, audit, new SensitiveDiagnostics());
        await viewModel.InitializeAsync();

        await viewModel.PreviewImportAsync(@"C:\Private\customer-secret.rdp");
        await viewModel.DiagnoseAsync(profile);

        var details = string.Join("\n", audit.Events.Select(x => x.RedactedDetail));
        Assert.DoesNotContain("customer-secret", details, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive-host", details, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("192.0.2.40", details, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source_type=.rdp", details, StringComparison.Ordinal);
        Assert.Contains("address_count=1", details, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionAccessibilitySummaryIncludesOperationalContextWithoutNotesOrUsername()
    {
        var profile = new ConnectionProfile
        {
            Name = "Production SQL",
            Host = "sql.example.test",
            Port = 3390,
            Environment = ConnectionEnvironment.Production,
            IsFavorite = true,
            UsernameHint = "DOMAIN\\private-user",
            Notes = "private runbook"
        };

        var summary = Assert.IsType<string>(new ConnectionAccessibilityConverter().Convert(profile, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Contains("Production SQL", summary, StringComparison.Ordinal);
        Assert.Contains("sql.example.test", summary, StringComparison.Ordinal);
        Assert.Contains("port 3390", summary, StringComparison.Ordinal);
        Assert.Contains("favorite", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("private-user", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("private runbook", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperationalDetailsIncludeSafeMetadataAndExcludeUsernameAndNotes()
    {
        var viewModel = new MainViewModel(
            new MemoryRepository(),
            new StubImportService(),
            new MemoryAuditStore(),
            new StubDiagnostics(),
            new AllowPolicy(),
            new StubExportService(),
            new StubManagedWorkspaceClient());
        await viewModel.InitializeAsync();
        var managed = Assert.Single(viewModel.Connections);
        managed.Tags = ["Managed", "database"];
        managed.UsernameHint = "DOMAIN\\private-user";
        managed.Notes = "private incident runbook";
        managed.LastConnectedAtUtc = new DateTimeOffset(2026, 7, 20, 10, 30, 0, TimeSpan.Zero);

        var details = Assert.IsType<string>(new ConnectionOperationalDetailsConverter().Convert(
            [managed, viewModel], typeof(string), null!, CultureInfo.InvariantCulture));

        Assert.Contains("managed.example.test:3389", details, StringComparison.Ordinal);
        Assert.Contains("Environment: Test", details, StringComparison.Ordinal);
        Assert.Contains("Source: Managed workspace", details, StringComparison.Ordinal);
        Assert.Contains("Tags: database", details, StringComparison.Ordinal);
        Assert.Contains("Last used:", details, StringComparison.Ordinal);
        Assert.DoesNotContain("private-user", details, StringComparison.Ordinal);
        Assert.DoesNotContain("private incident runbook", details, StringComparison.Ordinal);
        Assert.Equal(System.Windows.Visibility.Visible, new ManagedConnectionVisibilityConverter().Convert(
            [managed, viewModel], typeof(System.Windows.Visibility), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void HostPortFormattingIsDeterministicForDnsIpv4AndIpv6Targets()
    {
        var converter = new HostPortConverter();

        Assert.Equal("server.example.test:3390", converter.Convert(
            new ConnectionProfile { Host = "server.example.test", Port = 3390 }, typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Equal("192.0.2.25:3389", converter.Convert(
            new ConnectionProfile { Host = "192.0.2.25", Port = 3389 }, typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Equal("[2001:db8::25]:3389", converter.Convert(
            new ConnectionProfile { Host = "2001:db8::25", Port = 3389 }, typeof(string), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task SelectionCapabilitiesDifferentiateNoSelectionLocalManagedAndReadOnlyProfiles()
    {
        var repository = new MemoryRepository();
        await repository.SaveAsync(new ConnectionProfile { Name = "Local", Host = "local.example.test" });
        var localViewModel = Create(repository);
        await localViewModel.InitializeAsync();

        Assert.True(localViewModel.HasSelectedConnection);
        Assert.True(localViewModel.CanConnectSelected);
        Assert.True(localViewModel.CanEditSelected);
        Assert.True(localViewModel.CanModifySelectedLocal);

        localViewModel.SelectedConnection = null;
        Assert.False(localViewModel.HasSelectedConnection);
        Assert.False(localViewModel.CanConnectSelected);
        Assert.False(localViewModel.CanEditSelected);
        Assert.False(localViewModel.CanModifySelectedLocal);

        var managedViewModel = new MainViewModel(
            new MemoryRepository(),
            new StubImportService(),
            new MemoryAuditStore(),
            new StubDiagnostics(),
            new AllowPolicy(),
            new StubExportService(),
            new StubManagedWorkspaceClient());
        await managedViewModel.InitializeAsync();

        Assert.True(managedViewModel.HasSelectedConnection);
        Assert.True(managedViewModel.CanConnectSelected);
        Assert.True(managedViewModel.CanEditSelected);
        Assert.False(managedViewModel.CanModifySelectedLocal);

        managedViewModel.IsManagedReadOnly = true;
        Assert.False(managedViewModel.CanConnectSelected);
        Assert.True(managedViewModel.CanEditSelected);
        Assert.False(managedViewModel.CanModifySelectedLocal);
    }

    [Fact]
    public async Task FilteringRemainsCorrectWithFiveHundredConnections()
    {
        var repository = new MemoryRepository();
        for (var index = 0; index < 500; index++)
        {
            await repository.SaveAsync(new ConnectionProfile
            {
                Name = $"Server {index:D4}",
                Host = $"host-{index:D4}.example.test",
                Port = 3389 + index % 3
            });
        }
        var viewModel = Create(repository);
        await viewModel.InitializeAsync();

        Assert.Equal(500, viewModel.FilteredConnections.Count);
        viewModel.SearchText = "host-0427";
        var match = Assert.Single(viewModel.FilteredConnections);
        Assert.Equal("Server 0427", match.Name);
        Assert.Equal("host-0427.example.test", match.Host);
        Assert.Same(match, viewModel.SelectedConnection);
    }

    [Fact]
    public void ImportPreviewAccessibilitySummaryTracksSelectionAndStatus()
    {
        var candidate = new ImportCandidate("rdp", "Lab server", "lab.example.test", 3390, null, ["Lab"], new RdpSettings(), []);
        var item = new ImportPreviewItem(candidate, true, false);

        Assert.Equal("Lab server, lab.example.test:3390, Lab, Ready, selected for import", item.AccessibilityName);

        item.IsSelected = false;

        Assert.EndsWith("not selected for import", item.AccessibilityName, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportPreviewDisablesDuplicateAndInvalidCandidates()
    {
        var duplicate = new ImportPreviewItem(new ImportCandidate("rdp", "Duplicate", "server.example.test", 3389, null, [], new RdpSettings(), []), false, true);
        var invalid = new ImportPreviewItem(new ImportCandidate("rdp", "Invalid", "bad host", 3389, null, [], new RdpSettings(), ["Host is invalid"]), false, false);
        var ready = new ImportPreviewItem(new ImportCandidate("rdp", "Ready", "ready.example.test", 3389, null, [], new RdpSettings(), []), true, false);

        Assert.False(duplicate.CanImport);
        Assert.False(invalid.CanImport);
        Assert.True(ready.CanImport);
    }

    private static MainViewModel Create(MemoryRepository repository, MemoryAuditStore? audit = null, IConnectivityDiagnostics? diagnostics = null) => new(
        repository,
        new StubImportService(),
        audit ?? new MemoryAuditStore(),
        diagnostics ?? new StubDiagnostics(),
        new AllowPolicy(),
        new StubExportService());

    private sealed class MemoryRepository : IConnectionRepository
    {
        public List<ConnectionProfile> Connections { get; } = [];
        public List<ConnectionFolder> Folders { get; } = [];
        public Task<IReadOnlyList<ConnectionProfile>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ConnectionProfile>>(Connections.ToArray());
        public Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Connections.FirstOrDefault(x => x.Id == id));
        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
        {
            var index = Connections.FindIndex(x => x.Id == profile.Id);
            if (index >= 0) Connections[index] = profile; else Connections.Add(profile);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) { Connections.RemoveAll(x => x.Id == id); return Task.CompletedTask; }
        public Task<IReadOnlyList<ConnectionFolder>> ListFoldersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ConnectionFolder>>(Folders.ToArray());
        public Task SaveFolderAsync(ConnectionFolder folder, CancellationToken cancellationToken = default)
        {
            var index = Folders.FindIndex(x => x.Id == folder.Id);
            if (index >= 0) Folders[index] = folder; else Folders.Add(folder);
            return Task.CompletedTask;
        }
        public Task DeleteFolderAsync(Guid id, CancellationToken cancellationToken = default) { Folders.RemoveAll(x => x.Id == id); return Task.CompletedTask; }
    }

    private sealed class MemoryAuditStore : IAuditStore
    {
        public List<AuditEvent> Events { get; } = [];
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) { Events.Add(auditEvent); return Task.CompletedTask; }
        public Task<IReadOnlyList<AuditEvent>> ListRecentAsync(int limit = 500, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditEvent>>(Events.TakeLast(limit).Reverse().ToArray());
        public Task<int> CloseInterruptedSessionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class StubImportService : IImportService
    {
        public Task<ImportPreview> PreviewAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new ImportPreview(path, [], [], 0));
    }

    private sealed class StubDiagnostics : IConnectivityDiagnostics
    {
        public Task<DiagnosticResult> TestAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(new DiagnosticResult(host, port, true, ["127.0.0.1"], true, TimeSpan.Zero, "ok"));
    }

    private sealed class SensitiveDiagnostics : IConnectivityDiagnostics
    {
        public Task<DiagnosticResult> TestAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiagnosticResult(host, port, true, ["192.0.2.40"], true, TimeSpan.Zero, $"Connected to {host} at 192.0.2.40."));
    }

    private sealed class AllowPolicy : IPolicyDecisionPoint
    {
        public Task<PolicyDecision> AuthorizeLaunchAsync(ConnectionProfile connection, CancellationToken cancellationToken = default) => Task.FromResult(new PolicyDecision(true, "allowed", []));
    }

    private sealed class StubExportService : ILocalExportService
    {
        public Task ExportInventoryAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateRedactedSupportPackageAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubManagedWorkspaceClient : IManagedWorkspaceClient
    {
        private readonly Guid organizationId = Guid.NewGuid();
        private readonly Guid workspaceId = Guid.NewGuid();
        private readonly Guid connectionId = Guid.NewGuid();

        public bool IsSignedIn => true;
        public ManagedWorkspaceSelection? Selection => new(organizationId, workspaceId, "Test organization", "Test workspace");

        public Task<ManagedSyncResult> LoadCachedAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateResult());
        public Task<ManagedSyncResult> SynchronizeAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateResult());
        public Task<(IReadOnlyList<OrganizationDto> Organizations, IReadOnlyList<WorkspaceDto> Workspaces)> SignInAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkspaceDto>> ListWorkspacesAsync(Guid organizationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SelectWorkspaceAsync(OrganizationDto organization, WorkspaceDto workspace, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagedProductivitySnapshot> LoadProductivityAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UserWorkspaceStateDto> SaveWorkspaceStateAsync(SaveUserWorkspaceStateRequest request, long? expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BulkConnectPreviewResult> PreviewBulkConnectAsync(IReadOnlyList<Guid> connectionIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagedConnectionDto> CreateFromTemplateAsync(CreateConnectionFromTemplateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagedConnectionDto> CopyConnectionAsync(Guid connectionId, CopyManagedConnectionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        private ManagedSyncResult CreateResult()
        {
            var now = DateTimeOffset.UtcNow;
            var metadata = new ConnectionMetadataDto("", "", "", "", "", "", "", "", "", "", "", "", "", "", "");
            var settings = new ManagedRdpSettingsDto(
                NextRdp.Platform.Domain.ManagedConnectionKind.Desktop,
                true,
                false,
                "",
                NextRdp.Platform.Domain.GatewayUsage.Never,
                "",
                443,
                "",
                "",
                "",
                NextRdp.Platform.Domain.SessionDisplayMode.Embedded,
                [],
                null,
                null,
                32,
                NextRdp.Platform.Domain.PerformancePreset.Auto,
                true,
                NextRdp.Platform.Domain.ThumbnailMode.Enabled,
                NextRdp.Platform.Domain.ShortcutRouting.RemoteInFullScreen,
                true,
                false,
                false,
                false,
                false,
                false,
                false,
                true,
                true,
                true,
                NextRdp.Platform.Domain.TransferDirection.Bidirectional,
                NextRdp.Platform.Domain.TransferDirection.Bidirectional,
                60,
                3);
            var connection = new ManagedConnectionDto(
                connectionId,
                organizationId,
                workspaceId,
                null,
                null,
                NextRdp.Platform.Domain.ResourceVisibility.Shared,
                "Managed test",
                "managed.example.test",
                3389,
                "",
                "",
                ConnectionEnvironment.Test,
                "",
                [],
                metadata,
                settings,
                1,
                now);
            var response = new SyncResponse([], "cursor", now, "0.1.0", false, 1);
            var status = new ManagedCacheStatus(organizationId, workspaceId, true, false, false, now, "0.1.0", false, 1, "Synchronized.");
            return new ManagedSyncResult(response, status, [connection], []);
        }
    }

    private sealed class StubSessionHost : IRdpSessionHost
    {
        public event EventHandler<SessionState>? StateChanged;
        public event EventHandler<string>? DiagnosticMessage
        {
            add { }
            remove { }
        }
        public SessionState State { get; private set; } = SessionState.Connected;
        public bool DisconnectCalled { get; private set; }
        public bool Disposed { get; private set; }
        public Exception? DisposeFailure { get; init; }

        public Task ConnectAsync(ConnectionProfile connection, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCalled = true;
            State = SessionState.Disconnected;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Disposed = true;
            if (DisposeFailure is not null) throw DisposeFailure;
            GC.SuppressFinalize(this);
        }
    }
}
