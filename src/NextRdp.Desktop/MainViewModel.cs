using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Forms.Integration;
using CommunityToolkit.Mvvm.ComponentModel;
using NextRdp.Core;
using NextRdp.Platform.Application;
using NextRdp.Platform.Client;
using NextRdp.Platform.Domain;

namespace NextRdp.Desktop;

public sealed partial class MainViewModel(
    IConnectionRepository repository,
    IImportService importService,
    IAuditStore auditStore,
    IConnectivityDiagnostics diagnostics,
    IPolicyDecisionPoint policy,
    ILocalExportService exportService,
    IManagedWorkspaceClient managedClient) : ObservableObject
{
    public MainViewModel(
        IConnectionRepository repository,
        IImportService importService,
        IAuditStore auditStore,
        IConnectivityDiagnostics diagnostics,
        IPolicyDecisionPoint policy,
        ILocalExportService exportService)
        : this(repository, importService, auditStore, diagnostics, policy, exportService, new UnavailableManagedWorkspaceClient())
    {
    }

    private readonly List<ConnectionFolder> folders = [];
    private readonly Dictionary<Guid, ConnectionFolder> managedFolders = [];
    private readonly Dictionary<Guid, ConnectionProfile> managedProfiles = [];
    private readonly Dictionary<Guid, EffectivePolicy> managedPolicies = [];
    private readonly Dictionary<Guid, IReadOnlyList<Guid>> managedDynamicGroupMembers = [];
    private readonly Dictionary<Guid, DateTimeOffset> managedLastConnectedAtUtc = [];
    private UserWorkspaceStateDto? managedWorkspaceState;

    public ObservableCollection<ConnectionProfile> Connections { get; } = [];
    public ObservableCollection<ConnectionProfile> FilteredConnections { get; } = [];
    public ObservableCollection<FolderNavigationNode> FolderNavigation { get; } = [];
    public ObservableCollection<SessionTab> Sessions { get; } = [];
    public ObservableCollection<OrganizationDto> ManagedOrganizations { get; } = [];
    public ObservableCollection<WorkspaceDto> ManagedWorkspaces { get; } = [];
    public ObservableCollection<DynamicGroupDto> ManagedDynamicGroups { get; } = [];
    public ObservableCollection<ConnectionTemplateDto> ManagedTemplates { get; } = [];
    public ObservableCollection<WorkspaceConnectionDescriptor> WorkspaceDescriptors { get; } = [];

    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private ConnectionProfile? selectedConnection;
    [ObservableProperty] private SessionTab? selectedSession;
    [ObservableProperty] private FolderNavigationNode? selectedFolderNavigation;
    [ObservableProperty] private string quickName = "New connection";
    [ObservableProperty] private string quickHost = string.Empty;
    [ObservableProperty] private string quickPort = "3389";
    [ObservableProperty] private string statusMessage = "Ready.";
    [ObservableProperty] private OrganizationDto? selectedManagedOrganization;
    [ObservableProperty] private WorkspaceDto? selectedManagedWorkspace;
    [ObservableProperty] private string managedStatusMessage = "Managed workspace: signed out";
    [ObservableProperty] private bool isManagedOnline;
    [ObservableProperty] private bool isManagedReadOnly = true;
    [ObservableProperty] private bool isManagedAccessRevoked;

    public bool HasNoSessions => Sessions.Count == 0;
    public bool HasWorkspaceDescriptors => WorkspaceDescriptors.Count > 0;
    public bool HasSelectedConnection => SelectedConnection is not null;
    public bool CanConnectSelected => SelectedConnection is { } connection && CanLaunch(connection);
    public bool CanEditSelected => SelectedConnection is not null;
    public bool CanModifySelectedLocal => SelectedConnection is { } connection && !IsManagedConnection(connection);
    public bool CanSynchronizeManaged => SelectedManagedOrganization is not null && SelectedManagedWorkspace is not null;
    public IReadOnlyList<ConnectionFolder> Folders => folders;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedFolderNavigationChanged(FolderNavigationNode? value) => ApplyFilter();

    partial void OnSelectedConnectionChanged(ConnectionProfile? value) => NotifySelectedConnectionCapabilities();

    partial void OnSelectedManagedOrganizationChanged(OrganizationDto? value) => OnPropertyChanged(nameof(CanSynchronizeManaged));

    partial void OnSelectedManagedWorkspaceChanged(WorkspaceDto? value) => OnPropertyChanged(nameof(CanSynchronizeManaged));

    partial void OnIsManagedOnlineChanged(bool value) => OnPropertyChanged(nameof(CanConnectSelected));

    partial void OnIsManagedReadOnlyChanged(bool value) => OnPropertyChanged(nameof(CanConnectSelected));

    partial void OnIsManagedAccessRevokedChanged(bool value) => OnPropertyChanged(nameof(CanConnectSelected));

    private void NotifySelectedConnectionCapabilities()
    {
        OnPropertyChanged(nameof(HasSelectedConnection));
        OnPropertyChanged(nameof(CanConnectSelected));
        OnPropertyChanged(nameof(CanEditSelected));
        OnPropertyChanged(nameof(CanModifySelectedLocal));
    }

    public async Task InitializeAsync()
    {
        var recovered = await auditStore.CloseInterruptedSessionsAsync();
        await ReloadInventoryAsync();
        try
        {
            var cached = await managedClient.LoadCachedAsync();
            await ApplyManagedResultAsync(cached);
        }
        catch (InvalidOperationException)
        {
            // A first-run device legitimately has no managed cache yet.
        }
        StatusMessage = recovered == 0
            ? $"Ready. {Connections.Count} connection(s) loaded."
            : $"Recovered {recovered} session record(s) after the previous application exit. {Connections.Count} connection(s) loaded.";
    }

    public async Task ReloadInventoryAsync(Guid? preferredConnectionId = null)
    {
        preferredConnectionId ??= SelectedConnection?.Id;
        Connections.Clear();
        foreach (var connection in await repository.ListAsync()) Connections.Add(connection);
        foreach (var connection in managedProfiles.Values.OrderBy(connection => connection.Name, StringComparer.OrdinalIgnoreCase)) Connections.Add(connection);
        folders.Clear();
        folders.AddRange(await repository.ListFoldersAsync());
        folders.AddRange(managedFolders.Values.OrderBy(folder => folder.SortOrder).ThenBy(folder => folder.Name));
        BuildFolderNavigation();
        ApplyFilter();
        SelectedConnection = preferredConnectionId is { } id
            ? Connections.FirstOrDefault(x => x.Id == id)
            : FilteredConnections.FirstOrDefault();
    }

    public async Task<ConnectionProfile> SaveQuickConnectionAsync()
    {
        if (!int.TryParse(QuickPort, out var port)) throw new InvalidOperationException("The RDP port must be a number.");
        var profile = new ConnectionProfile { Name = QuickName.Trim(), Host = QuickHost.Trim(), Port = port };
        await repository.SaveAsync(profile);
        await ReloadInventoryAsync(profile.Id);
        return profile;
    }

    public async Task SaveConnectionAsync(ConnectionProfile profile)
    {
        EnsureLocalProfile(profile);
        await repository.SaveAsync(profile);
        await ReloadInventoryAsync(profile.Id);
        StatusMessage = $"Saved {profile.Name}.";
    }

    public async Task DeleteConnectionAsync(ConnectionProfile profile)
    {
        EnsureLocalProfile(profile);
        await repository.DeleteAsync(profile.Id);
        await ReloadInventoryAsync();
        StatusMessage = $"Deleted {profile.Name}.";
    }

    public async Task ToggleFavoriteAsync(ConnectionProfile profile)
    {
        EnsureLocalProfile(profile);
        profile.IsFavorite = !profile.IsFavorite;
        await repository.SaveAsync(profile);
        await ReloadInventoryAsync(profile.Id);
        StatusMessage = profile.IsFavorite ? $"Added {profile.Name} to favorites." : $"Removed {profile.Name} from favorites.";
    }

    public async Task MarkConnectedAsync(Guid connectionId)
    {
        var connectedAtUtc = DateTimeOffset.UtcNow;
        var profile = await repository.GetAsync(connectionId);
        if (profile is not null)
        {
            profile.LastConnectedAtUtc = connectedAtUtc;
            await repository.SaveAsync(profile);
        }
        else if (managedProfiles.ContainsKey(connectionId))
        {
            managedLastConnectedAtUtc[connectionId] = connectedAtUtc;
        }
        else
        {
            return;
        }
        var current = Connections.FirstOrDefault(x => x.Id == connectionId);
        if (current is not null) current.LastConnectedAtUtc = connectedAtUtc;
        ApplyFilter();
    }

    public Task<PolicyDecision> AuthorizeAsync(ConnectionProfile profile) => policy.AuthorizeLaunchAsync(profile);

    public async Task<ImportPreview> PreviewImportAsync(string path)
    {
        var preview = await importService.PreviewAsync(path);
        await auditStore.WriteAsync(new AuditEvent
        {
            Type = AuditEventType.ImportPreviewed,
            Outcome = preview.Errors.Count == 0 ? "success" : "rejected",
            RedactedDetail = $"source_type={Path.GetExtension(path).ToLowerInvariant()} candidates={preview.Candidates.Count} discarded={preview.SecretFieldsDiscarded}"
        });
        return preview;
    }

    public async Task<(int Imported, int Skipped)> CommitImportAsync(IEnumerable<ImportCandidate> candidates)
    {
        var selected = candidates.ToArray();
        var existing = Connections.Select(x => ConnectionIdentity.CreateKey(x.Host, x.Port)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var imported = 0;
        var skipped = 0;
        foreach (var candidate in selected)
        {
            if (candidate.Warnings.Count > 0)
            {
                skipped++;
                continue;
            }
            if (!existing.Add(ConnectionIdentity.CreateKey(candidate.Host, candidate.Port)))
            {
                skipped++;
                continue;
            }
            var folderId = await EnsureFolderPathAsync(candidate.FolderPath);
            var profile = new ConnectionProfile
            {
                Name = candidate.Name,
                Host = candidate.Host,
                Port = candidate.Port,
                UsernameHint = candidate.UsernameHint,
                Settings = candidate.Settings,
                FolderId = folderId
            };
            await repository.SaveAsync(profile);
            imported++;
        }
        await auditStore.WriteAsync(new AuditEvent
        {
            Type = AuditEventType.ImportCompleted,
            Outcome = "success",
            RedactedDetail = $"selected={selected.Length} imported={imported} skipped={skipped}"
        });
        await ReloadInventoryAsync();
        StatusMessage = $"Imported {imported} connection(s); skipped {skipped} duplicate or invalid item(s).";
        return (imported, skipped);
    }

    public bool IsDuplicate(ImportCandidate candidate) =>
        Connections.Any(x => string.Equals(ConnectionIdentity.CreateKey(x.Host, x.Port), ConnectionIdentity.CreateKey(candidate.Host, candidate.Port), StringComparison.OrdinalIgnoreCase));

    public async Task<DiagnosticResult> DiagnoseAsync(ConnectionProfile profile)
    {
        StatusMessage = $"Testing {profile.Host}:{profile.Port}…";
        var result = await diagnostics.TestAsync(profile.Host, profile.Port, TimeSpan.FromSeconds(5));
        StatusMessage = result.Message;
        await auditStore.WriteAsync(new AuditEvent
        {
            Type = AuditEventType.DiagnosticExecuted,
            ConnectionId = profile.Id,
            Outcome = result.PortReachable ? "success" : "failed",
            RedactedDetail = $"dns={result.DnsSucceeded} reachable={result.PortReachable} address_count={result.Addresses.Count} port={result.Port}"
        });
        return result;
    }

    public Task AuditAsync(AuditEvent auditEvent) => auditStore.WriteAsync(auditEvent);

    public Task ExportInventoryAsync(string path) => exportService.ExportInventoryAsync(path);

    public Task CreateSupportPackageAsync(string path) => exportService.CreateRedactedSupportPackageAsync(path);

    public bool IsManagedConnection(ConnectionProfile profile) => managedProfiles.ContainsKey(profile.Id);
    public bool IsManagedFolder(Guid folderId) => managedFolders.ContainsKey(folderId);

    public bool CanLaunch(ConnectionProfile profile) => !IsManagedConnection(profile) || IsManagedOnline && !IsManagedReadOnly && !IsManagedAccessRevoked;

    public string LaunchRestriction(ConnectionProfile profile) => !IsManagedConnection(profile) ? string.Empty : IsManagedAccessRevoked
        ? "Managed access was revoked. Sign in and synchronize before launching this resource."
        : IsManagedReadOnly || !IsManagedOnline
            ? "Managed connections cannot launch from an offline or obsolete read-only cache. Sign in and synchronize first."
            : string.Empty;

    public string ManagedPolicySummary(ConnectionProfile profile)
    {
        if (!managedPolicies.TryGetValue(profile.Id, out var effective)) return "No centrally published overrides apply.";
        var inherited = effective.Fields.Values.Count(field => !field.Locked);
        var locked = effective.Fields.Values.Count(field => field.Locked);
        var conflicts = effective.Conflicts.Count;
        return $"Effective policy revision {effective.Revision}: {inherited} inherited field(s), {locked} locked field(s), {conflicts} conflict(s).";
    }

    public async Task SignInManagedAsync()
    {
        ManagedStatusMessage = "Opening corporate sign-in...";
        var result = await managedClient.SignInAsync();
        ManagedOrganizations.Clear();
        foreach (var organization in result.Organizations) ManagedOrganizations.Add(organization);
        SelectedManagedOrganization = ManagedOrganizations.FirstOrDefault(item => item.Id == managedClient.Selection?.OrganizationId) ?? ManagedOrganizations.FirstOrDefault();
        ManagedWorkspaces.Clear();
        foreach (var workspace in result.Workspaces) ManagedWorkspaces.Add(workspace);
        SelectedManagedWorkspace = ManagedWorkspaces.FirstOrDefault(item => item.Id == managedClient.Selection?.WorkspaceId) ?? (ManagedWorkspaces.Count == 1 ? ManagedWorkspaces[0] : null);
        if (managedClient.Selection is not null) await SynchronizeManagedAsync();
        else ManagedStatusMessage = ManagedOrganizations.Count == 0 ? "Signed in, but no organization is assigned." : "Signed in. Choose an organization and workspace.";
    }

    public async Task SelectManagedOrganizationAsync(OrganizationDto? organization)
    {
        SelectedManagedOrganization = organization;
        ManagedWorkspaces.Clear();
        SelectedManagedWorkspace = null;
        if (organization is null) return;
        foreach (var workspace in await managedClient.ListWorkspacesAsync(organization.Id)) ManagedWorkspaces.Add(workspace);
        if (ManagedWorkspaces.Count == 1)
        {
            SelectedManagedWorkspace = ManagedWorkspaces[0];
            await SelectManagedWorkspaceAsync(ManagedWorkspaces[0]);
        }
    }

    public async Task SelectManagedWorkspaceAsync(WorkspaceDto? workspace)
    {
        SelectedManagedWorkspace = workspace;
        if (workspace is null || SelectedManagedOrganization is null) return;
        await managedClient.SelectWorkspaceAsync(SelectedManagedOrganization, workspace);
        await SynchronizeManagedAsync();
    }

    public async Task SynchronizeManagedAsync()
    {
        ManagedStatusMessage = "Synchronizing managed inventory...";
        var result = await managedClient.SynchronizeAsync();
        await ApplyManagedResultAsync(result);
    }

    public async Task SignOutManagedAsync()
    {
        await managedClient.SignOutAsync();
        IsManagedOnline = false;
        IsManagedReadOnly = true;
        ManagedStatusMessage = "Managed workspace: signed out; cached metadata remains read-only";
    }

    public async Task TogglePinnedFolderAsync(Guid folderId)
    {
        if (managedWorkspaceState is null || !managedFolders.ContainsKey(folderId)) throw new InvalidOperationException("Select a managed folder after signing in and synchronizing.");
        var pins = managedWorkspaceState.PinnedFolderIds.ToHashSet();
        if (!pins.Add(folderId)) pins.Remove(folderId);
        var request = new SaveUserWorkspaceStateRequest(pins.Order().ToArray(), managedWorkspaceState.Layout, managedWorkspaceState.SavedWorkspaces, managedWorkspaceState.TabGroups, managedWorkspaceState.Revision);
        managedWorkspaceState = await managedClient.SaveWorkspaceStateAsync(request, managedWorkspaceState.Revision);
        BuildFolderNavigation();
        ManagedStatusMessage = pins.Contains(folderId) ? "Managed folder pinned." : "Managed folder unpinned.";
    }

    public Task<BulkConnectPreviewResult> PreviewBulkConnectAsync(IReadOnlyList<Guid> connectionIds) => managedClient.PreviewBulkConnectAsync(connectionIds);

    public async Task CreateFromTemplateAsync(ConnectionTemplateDto template, string name, string host, int port, string usernameHint)
    {
        await managedClient.CreateFromTemplateAsync(new CreateConnectionFromTemplateRequest(template.Id, null, ResourceVisibility.Personal, name, host, port, usernameHint));
        await SynchronizeManagedAsync();
    }

    public async Task CopyManagedConnectionAsync(ConnectionProfile profile, string name)
    {
        if (!IsManagedConnection(profile)) throw new InvalidOperationException("Only a managed profile can be copied into the managed workspace.");
        await managedClient.CopyConnectionAsync(profile.Id, new CopyManagedConnectionRequest(name, profile.FolderId, ResourceVisibility.Personal));
        await SynchronizeManagedAsync();
    }

    public async Task SaveWorkspaceLayoutAsync(string name, IReadOnlyList<Guid> connectionIds, SessionDisplayMode displayMode)
    {
        if (managedWorkspaceState is null) throw new InvalidOperationException("Sign in and synchronize before saving a workspace.");
        var saved = managedWorkspaceState.SavedWorkspaces.Where(item => !item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        saved.Add(new SavedWorkspaceDto(Guid.CreateVersion7(), name.Trim(), connectionIds.Where(managedProfiles.ContainsKey).Distinct().ToArray(), displayMode));
        var request = new SaveUserWorkspaceStateRequest(managedWorkspaceState.PinnedFolderIds, managedWorkspaceState.Layout with { ReopenLastWorkspace = true }, saved, managedWorkspaceState.TabGroups, managedWorkspaceState.Revision);
        managedWorkspaceState = await managedClient.SaveWorkspaceStateAsync(request, managedWorkspaceState.Revision);
        PopulateWorkspaceDescriptors();
    }

    public async Task<ConnectionFolder> SaveFolderAsync(string name, Guid? parentId, Guid? folderId = null)
    {
        if (folderId is Guid managedId && managedFolders.ContainsKey(managedId) || parentId is Guid managedParent && managedFolders.ContainsKey(managedParent))
            throw new InvalidOperationException("Managed folders are read-only in the desktop client.");
        name = name.Trim();
        var existing = folderId is { } id ? folders.Single(x => x.Id == id) : null;
        var folder = new ConnectionFolder
        {
            Id = existing?.Id ?? Guid.CreateVersion7(),
            Name = name,
            ParentId = parentId,
            SortOrder = existing?.SortOrder ?? 0
        };
        var errors = folder.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(name));
        await repository.SaveFolderAsync(folder);
        await ReloadInventoryAsync();
        StatusMessage = $"Saved folder {folder.Name}.";
        return folder;
    }

    public async Task DeleteFolderAsync(ConnectionFolder folder)
    {
        if (managedFolders.ContainsKey(folder.Id)) throw new InvalidOperationException("Managed folders are read-only in the desktop client.");
        await repository.DeleteFolderAsync(folder.Id);
        await ReloadInventoryAsync();
        StatusMessage = $"Deleted folder {folder.Name}.";
    }

    public IReadOnlyList<FolderChoice> GetFolderChoices(Guid? excludedFolderId = null)
    {
        var choices = new List<FolderChoice> { new(null, "No folder") };
        var excludedIds = excludedFolderId is { } rootId ? GetDescendantFolderIds(rootId) : [];
        foreach (var folder in folders.Where(x => !managedFolders.ContainsKey(x.Id) && !excludedIds.Contains(x.Id)).OrderBy(GetFolderPath, StringComparer.OrdinalIgnoreCase))
            choices.Add(new FolderChoice(folder.Id, GetFolderPath(folder)));
        return choices;
    }

    public void AddSession(SessionTab tab)
    {
        Sessions.Add(tab);
        SelectedSession = tab;
        OnPropertyChanged(nameof(HasNoSessions));
    }

    private async Task ApplyManagedResultAsync(ManagedSyncResult result)
    {
        managedFolders.Clear();
        foreach (var folder in result.Folders)
            managedFolders[folder.Id] = new ConnectionFolder { Id = folder.Id, ParentId = folder.ParentId, Name = folder.Name, SortOrder = folder.SortOrder };
        managedProfiles.Clear();
        managedPolicies.Clear();
        foreach (var policy in result.EffectivePolicies) managedPolicies[policy.Key] = policy.Value;
        foreach (var connection in result.Connections)
        {
            result.EffectivePolicies.TryGetValue(connection.Id, out var effectivePolicy);
            var profile = ToLocalLaunchProfile(connection, effectivePolicy);
            if (managedLastConnectedAtUtc.TryGetValue(profile.Id, out var lastConnectedAtUtc))
                profile.LastConnectedAtUtc = lastConnectedAtUtc;
            managedProfiles[profile.Id] = profile;
        }
        foreach (var removedId in managedLastConnectedAtUtc.Keys.Except(managedProfiles.Keys).ToArray())
            managedLastConnectedAtUtc.Remove(removedId);
        ApplyProductivity(result.Productivity);
        IsManagedOnline = result.Status.IsOnline;
        IsManagedReadOnly = result.Status.IsReadOnly;
        IsManagedAccessRevoked = result.Status.AccessRevoked;
        ManagedStatusMessage = result.Status.LastSyncAtUtc is null ? result.Status.Message : $"{result.Status.Message} Last sync: {result.Status.LastSyncAtUtc.Value.ToLocalTime():g}.";
        await ReloadInventoryAsync();
        StatusMessage = $"{managedProfiles.Count} managed connection(s). {ManagedStatusMessage}";
    }

    private void ApplyProductivity(ManagedProductivitySnapshot? snapshot)
    {
        ManagedDynamicGroups.Clear();
        ManagedTemplates.Clear();
        managedDynamicGroupMembers.Clear();
        if (snapshot is not null)
        {
            foreach (var group in snapshot.DynamicGroups)
            {
                ManagedDynamicGroups.Add(group);
                managedDynamicGroupMembers[group.Id] = group.MatchingConnectionIds;
            }
            foreach (var template in snapshot.Templates) ManagedTemplates.Add(template);
            managedWorkspaceState = snapshot.WorkspaceState;
        }
        PopulateWorkspaceDescriptors();
    }

    private void PopulateWorkspaceDescriptors()
    {
        WorkspaceDescriptors.Clear();
        if (managedWorkspaceState?.Layout.ReopenLastWorkspace == true)
        {
            var last = managedWorkspaceState.SavedWorkspaces.Count == 0 ? null : managedWorkspaceState.SavedWorkspaces[^1];
            if (last is not null)
                foreach (var id in last.ConnectionIds)
                    if (managedProfiles.TryGetValue(id, out var profile)) WorkspaceDescriptors.Add(new WorkspaceConnectionDescriptor(last.Id, last.Name, profile));
        }
        OnPropertyChanged(nameof(HasWorkspaceDescriptors));
    }

    private static ConnectionProfile ToLocalLaunchProfile(ManagedConnectionDto connection, EffectivePolicy? effectivePolicy)
    {
        var settings = connection.Settings;
        return new ConnectionProfile
        {
            Id = connection.Id,
            Name = connection.Name,
            Host = connection.Host,
            Port = connection.Port,
            UsernameHint = connection.UsernameHint,
            Environment = connection.Environment,
            Notes = connection.Notes,
            Tags = connection.Tags.Concat(["Managed"]).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SearchableMetadata = string.Join(' ', new[] {
                connection.Metadata.BusinessUnit, connection.Metadata.ApplicationOwner, connection.Metadata.ServiceOwner,
                connection.Metadata.Criticality, connection.Metadata.Geography, connection.Metadata.DataClassification,
                connection.Metadata.MaintenanceWindow, connection.Metadata.SupportGroup, connection.Metadata.EscalationContact,
                connection.Metadata.AssetId, connection.Metadata.CmdbId, connection.Metadata.OperatingSystem
            }.Where(value => !string.IsNullOrWhiteSpace(value))),
            Settings = ApplyEffectivePolicy(new RdpSettings
            {
                Kind = Enum.Parse<RdpConnectionKind>(settings.Kind.ToString()),
                RequireNetworkLevelAuthentication = settings.RequireNetworkLevelAuthentication,
                AdministrativeSession = settings.AdministrativeSession,
                BrokerLoadBalanceInfo = settings.BrokerLoadBalanceInfo,
                GatewayUsage = Enum.Parse<RdpGatewayUsage>(settings.GatewayUsage.ToString()),
                GatewayHost = settings.GatewayHost,
                GatewayPort = settings.GatewayPort,
                RemoteAppProgram = settings.RemoteAppProgram,
                RemoteAppWorkingDirectory = settings.RemoteAppWorkingDirectory,
                RemoteAppArguments = settings.RemoteAppArguments,
                DisplayMode = Enum.Parse<RdpDisplayMode>(settings.DisplayMode.ToString()),
                SelectedMonitorIds = settings.SelectedMonitorIds.ToArray(),
                DesktopWidth = settings.DesktopWidth,
                DesktopHeight = settings.DesktopHeight,
                SmartSizing = true,
                RedirectClipboard = settings.ClipboardDirection != NextRdp.Platform.Domain.TransferDirection.Disabled,
                RedirectDrives = settings.RedirectDrives,
                RedirectPrinters = settings.RedirectPrinters,
                RedirectAudio = settings.RedirectAudio,
                RedirectMicrophone = settings.RedirectMicrophone,
                RedirectCamera = settings.RedirectCamera,
                RedirectComPorts = settings.RedirectComPorts,
                RedirectLptPorts = settings.RedirectLptPorts,
                RedirectUsbDevices = settings.RedirectUsbDevices,
                RedirectSmartCards = settings.RedirectSmartCards,
                ClipboardDirection = Enum.Parse<RdpTransferDirection>(settings.ClipboardDirection.ToString()),
                FileTransferDirection = Enum.Parse<RdpTransferDirection>(settings.FileTransferDirection.ToString()),
                ColorDepth = settings.ColorDepth,
                PerformancePreset = Enum.Parse<RdpPerformancePreset>(settings.PerformancePreset.ToString()),
                ShowConnectionQuality = settings.ShowConnectionQuality,
                ThumbnailMode = Enum.Parse<RdpThumbnailMode>(settings.ThumbnailMode.ToString()),
                ShortcutRouting = Enum.Parse<RdpShortcutRouting>(settings.ShortcutRouting.ToString()),
                AllowSecureAttentionSequence = settings.AllowSecureAttentionSequence,
                SessionTimeoutMinutes = settings.SessionTimeoutMinutes,
                ReconnectAttempts = settings.ReconnectAttempts
            }, effectivePolicy),
            CreatedAtUtc = connection.UpdatedAtUtc,
            UpdatedAtUtc = connection.UpdatedAtUtc
        };
    }

    private static RdpSettings ApplyEffectivePolicy(RdpSettings settings, EffectivePolicy? policy)
    {
        if (policy is null) return settings;
        bool Bool(string key, bool current) => policy.Fields.TryGetValue(key, out var field) ? JsonSerializer.Deserialize<bool>(field.JsonValue) : current;
        int Int(string key, int current) => policy.Fields.TryGetValue(key, out var field) ? JsonSerializer.Deserialize<int>(field.JsonValue) : current;
        int? NullableInt(string key, int? current) => policy.Fields.TryGetValue(key, out var field) ? JsonSerializer.Deserialize<int>(field.JsonValue) : current;
        T EnumValue<T>(string key, T current) where T : struct, Enum => policy.Fields.TryGetValue(key, out var field) && Enum.TryParse<T>(JsonSerializer.Deserialize<string>(field.JsonValue), out var value) ? value : current;
        string Text(string key, string current) => policy.Fields.TryGetValue(key, out var field) ? JsonSerializer.Deserialize<string>(field.JsonValue) ?? current : current;
        var clipboard = EnumValue("redirection.clipboard", settings.ClipboardDirection);
        return settings with
        {
            RequireNetworkLevelAuthentication = Bool("rdp.requireNla", settings.RequireNetworkLevelAuthentication),
            AdministrativeSession = Bool("rdp.administrativeSession", settings.AdministrativeSession),
            GatewayUsage = EnumValue("rdp.gatewayUsage", settings.GatewayUsage),
            GatewayHost = Text("rdp.gatewayHost", settings.GatewayHost),
            GatewayPort = Int("rdp.gatewayPort", settings.GatewayPort),
            DisplayMode = EnumValue("display.mode", settings.DisplayMode),
            DesktopWidth = NullableInt("display.width", settings.DesktopWidth),
            DesktopHeight = NullableInt("display.height", settings.DesktopHeight),
            ColorDepth = Int("display.colorDepth", settings.ColorDepth),
            PerformancePreset = EnumValue("performance.preset", settings.PerformancePreset),
            ThumbnailMode = EnumValue("privacy.thumbnailMode", settings.ThumbnailMode),
            ShortcutRouting = EnumValue("input.shortcutRouting", settings.ShortcutRouting),
            AllowSecureAttentionSequence = Bool("input.allowSas", settings.AllowSecureAttentionSequence),
            RedirectMicrophone = Bool("redirection.microphone", settings.RedirectMicrophone),
            RedirectCamera = Bool("redirection.camera", settings.RedirectCamera),
            RedirectComPorts = Bool("redirection.comPorts", settings.RedirectComPorts),
            RedirectLptPorts = Bool("redirection.lptPorts", settings.RedirectLptPorts),
            RedirectUsbDevices = Bool("redirection.usb", settings.RedirectUsbDevices),
            RedirectSmartCards = Bool("redirection.smartCards", settings.RedirectSmartCards),
            RedirectPrinters = Bool("redirection.printers", settings.RedirectPrinters),
            RedirectDrives = Bool("redirection.drives", settings.RedirectDrives),
            RedirectAudio = Bool("redirection.audio", settings.RedirectAudio),
            RedirectClipboard = clipboard != RdpTransferDirection.Disabled,
            ClipboardDirection = clipboard,
            FileTransferDirection = EnumValue("redirection.fileTransfer", settings.FileTransferDirection),
            SessionTimeoutMinutes = Int("session.timeoutMinutes", settings.SessionTimeoutMinutes),
            ReconnectAttempts = Int("session.reconnectAttempts", settings.ReconnectAttempts)
        };
    }

    private void EnsureLocalProfile(ConnectionProfile profile)
    {
        if (IsManagedConnection(profile)) throw new InvalidOperationException("Managed connections are read-only in the desktop client. Use the administration console to change them.");
    }

    public async Task RemoveSessionAsync(SessionTab tab)
    {
        var cleanupWasIncomplete = false;
        try
        {
            await tab.SessionHost.DisconnectAsync();
        }
        catch (Exception error) when (IsRecoverableSessionCleanupFailure(error))
        {
            cleanupWasIncomplete = true;
        }

        try
        {
            await tab.StateChanges.WhenIdleAsync();
        }
        catch (Exception error) when (IsRecoverableSessionCleanupFailure(error))
        {
            cleanupWasIncomplete = true;
        }

        Sessions.Remove(tab);
        try
        {
            tab.Dispose();
        }
        catch (Exception error) when (IsRecoverableSessionCleanupFailure(error))
        {
            cleanupWasIncomplete = true;
        }

        SelectedSession = Sessions.LastOrDefault();
        OnPropertyChanged(nameof(HasNoSessions));
        if (cleanupWasIncomplete)
            StatusMessage = "The session closed and its local control was released, but Windows could not complete every graceful cleanup step.";
    }

    private static bool IsRecoverableSessionCleanupFailure(Exception error) => error is not
        (OutOfMemoryException or AccessViolationException);

    private async Task<Guid?> EnsureFolderPathAsync(IReadOnlyList<string> path)
    {
        Guid? parentId = null;
        foreach (var rawSegment in path)
        {
            var segment = rawSegment.Trim();
            if (segment.Length == 0) continue;
            var folder = folders.FirstOrDefault(x => x.ParentId == parentId && x.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (folder is null)
            {
                folder = new ConnectionFolder { Name = segment, ParentId = parentId };
                await repository.SaveFolderAsync(folder);
                folders.Add(folder);
            }
            parentId = folder.Id;
        }
        return parentId;
    }

    private void BuildFolderNavigation()
    {
        var previous = SelectedFolderNavigation;
        FolderNavigation.Clear();
        FolderNavigation.Add(new("All connections", FolderNavigationKind.All));
        FolderNavigation.Add(new("Favorites", FolderNavigationKind.Favorites));
        FolderNavigation.Add(new("Recent", FolderNavigationKind.Recent));
        foreach (var group in ManagedDynamicGroups.OrderBy(group => group.Name)) FolderNavigation.Add(new($"Dynamic: {group.Name}", FolderNavigationKind.DynamicGroup, group.Id));
        foreach (var folder in folders.Where(x => x.ParentId is null).OrderBy(x => x.SortOrder).ThenBy(x => x.Name))
            FolderNavigation.Add(BuildFolderNode(folder));
        SelectedFolderNavigation = previous is null
            ? FolderNavigation[0]
            : FindNavigationNode(FolderNavigation, previous.Kind, previous.FolderId) ?? FolderNavigation[0];
    }

    private FolderNavigationNode BuildFolderNode(ConnectionFolder folder)
    {
        var pinned = managedWorkspaceState?.PinnedFolderIds.Contains(folder.Id) == true ? "Pinned: " : string.Empty;
        var node = new FolderNavigationNode($"{pinned}{folder.Name}", FolderNavigationKind.Folder, folder.Id);
        foreach (var child in folders.Where(x => x.ParentId == folder.Id).OrderBy(x => x.SortOrder).ThenBy(x => x.Name))
            node.Children.Add(BuildFolderNode(child));
        return node;
    }

    private void ApplyFilter()
    {
        var selectedId = SelectedConnection?.Id;
        IEnumerable<ConnectionProfile> matches = Connections;
        var navigation = SelectedFolderNavigation;
        if (navigation?.Kind == FolderNavigationKind.Favorites) matches = matches.Where(x => x.IsFavorite);
        else if (navigation?.Kind == FolderNavigationKind.Recent) matches = matches.Where(x => x.LastConnectedAtUtc != null);
        else if (navigation?.Kind == FolderNavigationKind.Folder && navigation.FolderId is { } folderId)
        {
            var folderIds = GetDescendantFolderIds(folderId);
            matches = matches.Where(x => x.FolderId is { } id && folderIds.Contains(id));
        }
        else if (navigation?.Kind == FolderNavigationKind.DynamicGroup && navigation.FolderId is { } groupId && managedDynamicGroupMembers.TryGetValue(groupId, out var members))
            matches = matches.Where(connection => members.Contains(connection.Id));

        var query = SearchText.Trim();
        if (query.Length > 0) matches = matches.Where(x => ConnectionSearch.Matches(x, query));

        matches = navigation?.Kind == FolderNavigationKind.Recent
            ? matches.OrderByDescending(x => x.LastConnectedAtUtc)
            : matches.OrderByDescending(x => x.IsFavorite).ThenBy(x => x.Name);
        FilteredConnections.Clear();
        foreach (var match in matches) FilteredConnections.Add(match);
        SelectedConnection = selectedId is { } id
            ? FilteredConnections.FirstOrDefault(connection => connection.Id == id) ?? FilteredConnections.FirstOrDefault()
            : FilteredConnections.FirstOrDefault();
    }

    private HashSet<Guid> GetDescendantFolderIds(Guid rootId)
    {
        var ids = new HashSet<Guid> { rootId };
        var queue = new Queue<Guid>();
        queue.Enqueue(rootId);
        while (queue.TryDequeue(out var current))
            foreach (var child in folders.Where(x => x.ParentId == current))
                if (ids.Add(child.Id)) queue.Enqueue(child.Id);
        return ids;
    }

    private string GetFolderPath(ConnectionFolder folder)
    {
        var names = new Stack<string>();
        ConnectionFolder? current = folder;
        var visited = new HashSet<Guid>();
        while (current is not null && visited.Add(current.Id))
        {
            names.Push(current.Name);
            current = current.ParentId is { } id ? folders.FirstOrDefault(x => x.Id == id) : null;
        }
        return string.Join(" / ", names);
    }

    private static FolderNavigationNode? FindNavigationNode(IEnumerable<FolderNavigationNode> nodes, FolderNavigationKind kind, Guid? folderId)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == kind && node.FolderId == folderId) return node;
            var child = FindNavigationNode(node.Children, kind, folderId);
            if (child is not null) return child;
        }
        return null;
    }
}

internal sealed class UnavailableManagedWorkspaceClient : IManagedWorkspaceClient
{
    public bool IsSignedIn => false;
    public ManagedWorkspaceSelection? Selection => null;
    public Task<(IReadOnlyList<OrganizationDto> Organizations, IReadOnlyList<WorkspaceDto> Workspaces)> SignInAsync(CancellationToken cancellationToken = default) => throw Unavailable();
    public Task<IReadOnlyList<WorkspaceDto>> ListWorkspacesAsync(Guid organizationId, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task SelectWorkspaceAsync(OrganizationDto organization, WorkspaceDto workspace, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task<ManagedSyncResult> SynchronizeAsync(CancellationToken cancellationToken = default) => throw Unavailable();
    public Task<ManagedSyncResult> LoadCachedAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No synchronized managed workspace is cached on this device.");
    public Task<ManagedProductivitySnapshot> LoadProductivityAsync(CancellationToken cancellationToken = default) => throw Unavailable();
    public Task<UserWorkspaceStateDto> SaveWorkspaceStateAsync(SaveUserWorkspaceStateRequest request, long? expectedRevision, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task<BulkConnectPreviewResult> PreviewBulkConnectAsync(IReadOnlyList<Guid> connectionIds, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task<ManagedConnectionDto> CreateFromTemplateAsync(CreateConnectionFromTemplateRequest request, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task<ManagedConnectionDto> CopyConnectionAsync(Guid connectionId, CopyManagedConnectionRequest request, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    private static InvalidOperationException Unavailable() => new("Managed workspace services are unavailable in this application context.");
}

public enum FolderNavigationKind { All, Favorites, Recent, Folder, DynamicGroup }

public sealed class FolderNavigationNode(string name, FolderNavigationKind kind, Guid? folderId = null)
{
    public string Name { get; } = name;
    public FolderNavigationKind Kind { get; } = kind;
    public Guid? FolderId { get; } = folderId;
    public ObservableCollection<FolderNavigationNode> Children { get; } = [];
}

public sealed record WorkspaceConnectionDescriptor(Guid WorkspaceId, string WorkspaceName, ConnectionProfile Connection)
{
    public string DisplayName => $"{WorkspaceName}: {Connection.Name} (disconnected)";
}

public sealed partial class SessionTab(string name, Guid connectionId, ConnectionEnvironment environment, bool showProductionWatermark, WindowsFormsHost hostView, IRdpSessionHost sessionHost, RdpThumbnailMode thumbnailMode = RdpThumbnailMode.Disabled, bool initiallyDetached = false) : ObservableObject, IDisposable
{
    private bool disposed;
    public Guid SessionId { get; } = Guid.CreateVersion7();
    public string Name { get; } = name;
    public Guid ConnectionId { get; } = connectionId;
    public ConnectionEnvironment Environment { get; } = environment;
    public bool ShowProductionWatermark { get; } = showProductionWatermark;
    public RdpThumbnailMode ThumbnailMode { get; } = thumbnailMode;
    public bool IsThumbnailEnabled => ThumbnailMode == RdpThumbnailMode.Enabled;
    public WindowsFormsHost HostView { get; } = hostView;
    [ObservableProperty] private object sessionContent = initiallyDetached ? CreateDetachedPlaceholder() : hostView;
    [ObservableProperty] private bool isDetached = initiallyDetached;
    public IRdpSessionHost SessionHost { get; } = sessionHost;
    internal OrderedAsyncQueue StateChanges { get; } = new();
    [ObservableProperty] private string status = "Connecting";
    [ObservableProperty] private System.Windows.Media.ImageSource? thumbnail;
    [ObservableProperty] private string thumbnailStatus = "Hidden until connected";
    public string AccessibilityName => $"{Name}, {Environment}, {Status}";

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(AccessibilityName));

    public void SetThumbnail(System.Windows.Media.ImageSource image)
    {
        if (!IsThumbnailEnabled) return;
        Thumbnail = image;
        ThumbnailStatus = string.Empty;
    }

    public void ClearThumbnail(string status)
    {
        Thumbnail = null;
        ThumbnailStatus = IsThumbnailEnabled ? status : string.Empty;
    }

    public void Reattach()
    {
        SessionContent = HostView;
        IsDetached = false;
    }

    public void Detach()
    {
        SessionContent = CreateDetachedPlaceholder();
        IsDetached = true;
    }

    private static System.Windows.Controls.Border CreateDetachedPlaceholder() => new System.Windows.Controls.Border
    {
        Padding = new System.Windows.Thickness(24),
        Child = new System.Windows.Controls.TextBlock
        {
            Text = "This session is open in a separate NextRDP window. Closing that window returns the live session to this tab.",
            TextWrapping = System.Windows.TextWrapping.Wrap,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        }
    };

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            SessionHost.Dispose();
        }
        finally
        {
            try { HostView.Dispose(); }
            finally { GC.SuppressFinalize(this); }
        }
    }
}
