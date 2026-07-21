using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NextRdp.Core;
using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Infrastructure.Tests;

public sealed class PlatformManagementServiceTests
{
    [Fact]
    public async Task SharedAndPersonalConnectionsRemainScopedAndSynchronizeWithoutSecrets()
    {
        await using var fixture = new ServiceFixture();
        var (organization, workspace) = await fixture.CreateScopeAsync();
        var first = ServiceFixture.Actor(organization.Id, WorkspaceRole.WorkspaceAdministrator);
        var second = ServiceFixture.Actor(organization.Id, WorkspaceRole.WorkspaceAdministrator);

        var shared = await fixture.Service.SaveConnectionAsync(first, organization.Id, workspace.Id, null, Connection("Shared", ResourceVisibility.Shared));
        var personal = await fixture.Service.SaveConnectionAsync(first, organization.Id, workspace.Id, null, Connection("Personal", ResourceVisibility.Personal));
        await fixture.Service.SaveConnectionAsync(second, organization.Id, workspace.Id, null, Connection("Other personal", ResourceVisibility.Personal));

        var firstList = await fixture.Service.ListConnectionsAsync(first, organization.Id, workspace.Id, null, null, 50);
        var secondList = await fixture.Service.ListConnectionsAsync(second, organization.Id, workspace.Id, null, null, 50);
        var sync = await fixture.Service.SynchronizeAsync(first, organization.Id, workspace.Id, new SyncRequest(null, "0.1.0", new Dictionary<Guid, long>()));
        var serialized = JsonSerializer.Serialize(sync);

        Assert.Equal(2, firstList.Items.Count);
        Assert.Contains(firstList.Items, item => item.Id == shared.Id);
        Assert.Contains(firstList.Items, item => item.Id == personal.Id);
        Assert.Equal(2, secondList.Items.Count);
        Assert.Contains(secondList.Items, item => item.Id == shared.Id);
        Assert.DoesNotContain(secondList.Items, item => item.Id == personal.Id);
        Assert.DoesNotContain("password", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, sync.Items.Count(item => item.ResourceType == "connection"));
    }

    [Fact]
    public async Task UpdateRequiresCurrentRevision()
    {
        await using var fixture = new ServiceFixture();
        var (organization, workspace) = await fixture.CreateScopeAsync();
        var actor = ServiceFixture.Actor(organization.Id, WorkspaceRole.WorkspaceAdministrator);
        var created = await fixture.Service.SaveConnectionAsync(actor, organization.Id, workspace.Id, null, Connection("Server", ResourceVisibility.Shared));

        var missing = await Assert.ThrowsAsync<PlatformProblemException>(() => fixture.Service.SaveConnectionAsync(actor, organization.Id, workspace.Id, created.Id, Connection("Changed", ResourceVisibility.Shared)));
        var stale = await Assert.ThrowsAsync<PlatformProblemException>(() => fixture.Service.SaveConnectionAsync(actor, organization.Id, workspace.Id, created.Id, Connection("Changed", ResourceVisibility.Shared) with { ExpectedRevision = 99 }));

        Assert.Equal(428, missing.StatusCode);
        Assert.Equal(409, stale.StatusCode);
    }

    [Fact]
    public async Task PublishedPolicyHasImmutableVersionAndEffectiveProvenance()
    {
        await using var fixture = new ServiceFixture();
        var (organization, workspace) = await fixture.CreateScopeAsync();
        var actor = ServiceFixture.Actor(organization.Id, WorkspaceRole.WorkspaceAdministrator);
        using var values = JsonDocument.Parse("{\"redirection.clipboard\":\"Disabled\",\"client.minimumVersion\":\"0.2.0\"}");
        var draft = await fixture.Service.SaveConfigurationDraftAsync(actor, organization.Id, workspace.Id, null,
            new SaveConfigurationDraftRequest(ConfigurationScopeType.Workspace, workspace.Id, 0, values.RootElement.Clone(), ["redirection.clipboard"], "Secure baseline", null));

        var published = await fixture.Service.PublishConfigurationAsync(actor, organization.Id, workspace.Id, draft.Id);
        var effective = await fixture.Service.GetEffectivePolicyAsync(actor, organization.Id, workspace.Id, null, null);

        Assert.Equal(1, published.Version);
        Assert.Equal(ConfigurationScopeType.Workspace, effective.Fields["redirection.clipboard"].Source);
        Assert.True(effective.Fields["redirection.clipboard"].Locked);
        Assert.NotEqual(0, effective.Revision);
    }

    [Fact]
    public async Task MinimumClientVersionUsesSemanticPrecedenceAndReadOnlyDegradation()
    {
        await using var fixture = new ServiceFixture();
        var (organization, workspace) = await fixture.CreateScopeAsync();
        var actor = ServiceFixture.Actor(organization.Id, WorkspaceRole.WorkspaceAdministrator);
        using var values = JsonDocument.Parse("{\"client.minimumVersion\":\"2.1.0-rc.1\"}");
        var draft = await fixture.Service.SaveConfigurationDraftAsync(actor, organization.Id, workspace.Id, null,
            new SaveConfigurationDraftRequest(ConfigurationScopeType.Workspace, workspace.Id, 0, values.RootElement.Clone(), ["client.minimumVersion"], "Client floor", null));
        await fixture.Service.PublishConfigurationAsync(actor, organization.Id, workspace.Id, draft.Id);

        var older = await fixture.Service.SynchronizeAsync(actor, organization.Id, workspace.Id, new SyncRequest(null, "2.1.0-beta.2", new Dictionary<Guid, long>()));
        var exact = await fixture.Service.SynchronizeAsync(actor, organization.Id, workspace.Id, new SyncRequest(null, "2.1.0-rc.1", new Dictionary<Guid, long>()));
        var release = await fixture.Service.SynchronizeAsync(actor, organization.Id, workspace.Id, new SyncRequest(null, "2.1.0", new Dictionary<Guid, long>()));

        Assert.True(older.ClientUpgradeRequired);
        Assert.False(exact.ClientUpgradeRequired);
        Assert.False(release.ClientUpgradeRequired);
        Assert.Equal("2.1.0-rc.1", older.MinimumClientVersion);
    }

    [Fact]
    public async Task ImportPreviewStripsSecretFieldsAndRejectsInvalidRows()
    {
        await using var fixture = new ServiceFixture();
        var (organization, workspace) = await fixture.CreateScopeAsync();
        var actor = ServiceFixture.Actor(organization.Id, WorkspaceRole.WorkspaceAdministrator);
        const string csv = "externalKey,name,host,password,port\nserver-1,Server,server.example.test,discard-me,3389\nbad,Bad host,host with spaces,discard-too,3389";

        var preview = await fixture.Service.PreviewImportAsync(actor, organization.Id, workspace.Id, new ImportPreviewRequest("csv", csv));

        Assert.Equal(2, preview.SecretFieldsDiscarded);
        Assert.Empty(preview.Candidates[0].Errors);
        Assert.NotNull(preview.Candidates[0].Connection);
        Assert.NotEmpty(preview.Candidates[1].Errors);
        Assert.Null(preview.Candidates[1].Connection);
        Assert.DoesNotContain("discard-me", JsonSerializer.Serialize(preview), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonAndCsvImportPreviewDetectExistingAndInFileDuplicates()
    {
        await using var fixture = new ServiceFixture();
        var (organization, workspace) = await fixture.CreateScopeAsync();
        var actor = ServiceFixture.Actor(organization.Id, WorkspaceRole.WorkspaceAdministrator);
        await fixture.Service.SaveConnectionAsync(actor, organization.Id, workspace.Id, null, Connection("Existing", ResourceVisibility.Shared) with
        {
            Host = "existing.example.test"
        });
        const string json = """
            [
              { "externalKey": "existing", "name": "Existing", "host": "EXISTING.EXAMPLE.TEST.", "port": 3389 },
              { "externalKey": "new-1", "name": "New", "host": "new.example.test", "port": 3389, "password": "discard" },
              { "externalKey": "new-2", "name": "Duplicate", "host": "new.example.test", "port": 3389 }
            ]
            """;
        const string csv = "externalKey,name,host,port\ncsv-1,CSV,csv.example.test,3389";

        var jsonPreview = await fixture.Service.PreviewImportAsync(actor, organization.Id, workspace.Id, new ImportPreviewRequest("json", json));
        var csvPreview = await fixture.Service.PreviewImportAsync(actor, organization.Id, workspace.Id, new ImportPreviewRequest("csv", csv));

        Assert.Contains("already exist", jsonPreview.Candidates[0].Errors[0], StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(jsonPreview.Candidates[1].Connection);
        Assert.Contains("duplicate", jsonPreview.Candidates[2].Errors[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, jsonPreview.SecretFieldsDiscarded);
        Assert.Equal("csv-1", Assert.Single(csvPreview.Candidates).ExternalKey);
    }

    [Fact]
    public async Task ServiceAccountSecretIsReturnedOnceAndNotPersistedInPlatformDatabase()
    {
        await using var fixture = new ServiceFixture();
        var (organization, _) = await fixture.CreateScopeAsync();
        var actor = ServiceFixture.Actor(organization.Id, WorkspaceRole.OrganizationAdministrator);

        var created = await fixture.Service.CreateServiceAccountAsync(actor, organization.Id,
            new CreateServiceAccountRequest("Automation", null, ["inventory.read"], DateTimeOffset.UtcNow.AddDays(1)));
        var persisted = await fixture.Context.ServiceAccounts.AsNoTracking().SingleAsync();
        var serialized = JsonSerializer.Serialize(persisted);

        Assert.NotEmpty(created.ClientSecret);
        Assert.Equal(created.ClientSecret, fixture.CredentialStore.LastSecret);
        Assert.DoesNotContain(created.ClientSecret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServiceAccountRelationshipIsGrantedAndRemovedWithLifecycle()
    {
        await using var fixture = new ServiceFixture(enableRelationships: true);
        var (organization, workspace) = await fixture.CreateScopeAsync();
        var actor = ServiceFixture.Actor(organization.Id, WorkspaceRole.OrganizationAdministrator);

        var created = await fixture.Service.CreateServiceAccountAsync(actor, organization.Id,
            new CreateServiceAccountRequest("Scoped automation", workspace.Id, ["inventory.read"], DateTimeOffset.UtcNow.AddDays(1)));
        await fixture.Service.RevokeServiceAccountAsync(actor, organization.Id, created.Id);

        Assert.Equal(2, fixture.Relationship.Ensured.Count(item => item.PrincipalId == created.Id));
        Assert.Contains(fixture.Relationship.Ensured, item => item.PrincipalId == created.Id && item.Role == WorkspaceRole.Operator && item.Target.WorkspaceId is null);
        Assert.Contains(fixture.Relationship.Ensured, item => item.PrincipalId == created.Id && item.Role == WorkspaceRole.OrganizationAdministrator && item.Target.WorkspaceId == workspace.Id);
        Assert.True(fixture.Relationship.LastEnsuredWasServiceAccount);
        Assert.Equal(2, fixture.Relationship.Removed.Count(item => item.PrincipalId == created.Id));
        Assert.Contains(fixture.Relationship.Removed, item => item.PrincipalId == created.Id && item.Role == WorkspaceRole.OrganizationAdministrator && item.Target.WorkspaceId == workspace.Id);
        Assert.Contains(fixture.Relationship.Removed, item => item.PrincipalId == created.Id && item.Role == WorkspaceRole.Operator && item.Target.WorkspaceId is null);
        Assert.True(fixture.Relationship.LastRemovedWasServiceAccount);
    }

    [Fact]
    public async Task OrganizationMembershipMaterializesAndRevokesEveryWorkspaceRelationship()
    {
        await using var fixture = new ServiceFixture(enableRelationships: true);
        var (organization, firstWorkspace) = await fixture.CreateScopeAsync();
        var actor = ServiceFixture.Actor(organization.Id, WorkspaceRole.OrganizationAdministrator);
        var user = new PlatformUser
        {
            Issuer = "https://identity.example.test",
            Subject = Guid.NewGuid().ToString("N"),
            DisplayName = "Organization operator",
            Status = MembershipStatus.Active
        };
        fixture.Context.Users.Add(user);
        await fixture.Context.SaveChangesAsync();
        fixture.Relationship.Ensured.Clear();

        var membership = await fixture.Service.SaveMembershipAsync(actor, organization.Id, null,
            new UpsertMembershipRequest(user.Id, null, WorkspaceRole.OrganizationAdministrator, MembershipStatus.Active, string.Empty, null));

        Assert.Contains(fixture.Relationship.Ensured, item => item.PrincipalId == user.Id && item.Target.ResourceType == "organization");
        Assert.Contains(fixture.Relationship.Ensured, item => item.PrincipalId == user.Id && item.Target.WorkspaceId == firstWorkspace.Id);

        var secondWorkspace = await fixture.Service.CreateWorkspaceAsync(actor, organization.Id,
            new CreateWorkspaceRequest("Recovery", "recovery", "Second authorization boundary"));
        Assert.Contains(fixture.Relationship.Ensured, item => item.PrincipalId == user.Id && item.Target.WorkspaceId == secondWorkspace.Id);

        fixture.Relationship.Removed.Clear();
        await fixture.Service.RevokeMembershipAsync(actor, organization.Id, membership.Id, membership.Revision);

        Assert.Equal(3, fixture.Relationship.Removed.Count(item => item.PrincipalId == user.Id));
        Assert.Contains(fixture.Relationship.Removed, item => item.PrincipalId == user.Id && item.Target.ResourceType == "organization");
        Assert.Contains(fixture.Relationship.Removed, item => item.PrincipalId == user.Id && item.Target.WorkspaceId == firstWorkspace.Id);
        Assert.Contains(fixture.Relationship.Removed, item => item.PrincipalId == user.Id && item.Target.WorkspaceId == secondWorkspace.Id);
    }

    [Fact]
    public async Task OrganizationServiceAccountMaterializesEveryWorkspaceRelationship()
    {
        await using var fixture = new ServiceFixture(enableRelationships: true);
        var (organization, firstWorkspace) = await fixture.CreateScopeAsync();
        var actor = ServiceFixture.Actor(organization.Id, WorkspaceRole.OrganizationAdministrator);
        fixture.Relationship.Ensured.Clear();

        var account = await fixture.Service.CreateServiceAccountAsync(actor, organization.Id,
            new CreateServiceAccountRequest("Organization automation", null, ["inventory.read"], DateTimeOffset.UtcNow.AddDays(1)));

        Assert.Contains(fixture.Relationship.Ensured, item => item.PrincipalId == account.Id && item.Target.ResourceType == "organization");
        Assert.Contains(fixture.Relationship.Ensured, item => item.PrincipalId == account.Id && item.Target.WorkspaceId == firstWorkspace.Id);

        var secondWorkspace = await fixture.Service.CreateWorkspaceAsync(actor, organization.Id,
            new CreateWorkspaceRequest("Automation", "automation", "Second automation boundary"));
        Assert.Contains(fixture.Relationship.Ensured, item => item.PrincipalId == account.Id && item.Target.WorkspaceId == secondWorkspace.Id);

        fixture.Relationship.Removed.Clear();
        await fixture.Service.RevokeServiceAccountAsync(actor, organization.Id, account.Id);

        Assert.Equal(3, fixture.Relationship.Removed.Count(item => item.PrincipalId == account.Id));
        Assert.Contains(fixture.Relationship.Removed, item => item.PrincipalId == account.Id && item.Target.ResourceType == "organization");
        Assert.Contains(fixture.Relationship.Removed, item => item.PrincipalId == account.Id && item.Target.WorkspaceId == firstWorkspace.Id);
        Assert.Contains(fixture.Relationship.Removed, item => item.PrincipalId == account.Id && item.Target.WorkspaceId == secondWorkspace.Id);
    }

    [Fact]
    public void ProtectedCursorRejectsTampering()
    {
        var codec = new ProtectedSyncCursorCodec(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        var cursor = codec.Encode(42);
        var replacement = cursor[^1] == 'A' ? 'B' : 'A';

        Assert.Equal(42, codec.Decode(cursor));
        Assert.Throws<PlatformProblemException>(() => codec.Decode(cursor[..^1] + replacement));
    }

    [Fact]
    public async Task BulkEditIsBoundedIdempotentAndRejectsKeyReuseWithDifferentPayload()
    {
        await using var fixture = new ServiceFixture();
        var (organization, workspace) = await fixture.CreateScopeAsync();
        var actor = ServiceFixture.Actor(organization.Id, WorkspaceRole.WorkspaceAdministrator);
        var connection = await fixture.Service.SaveConnectionAsync(actor, organization.Id, workspace.Id, null, Connection("Bulk target", ResourceVisibility.Shared));
        var request = new BulkConnectionEditRequest([connection.Id], ConnectionEnvironment.Test, null, false, ["updated"], [], null, null);

        var first = await fixture.Service.BulkEditConnectionsAsync(actor, organization.Id, workspace.Id, "bulk-edit-key-0001", request);
        var replay = await fixture.Service.BulkEditConnectionsAsync(actor, organization.Id, workspace.Id, "bulk-edit-key-0001", request);
        var conflict = await Assert.ThrowsAsync<PlatformProblemException>(() => fixture.Service.BulkEditConnectionsAsync(actor, organization.Id, workspace.Id, "bulk-edit-key-0001", request with { AddTags = ["different"] }));

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal(ConnectionEnvironment.Test, (await fixture.Service.GetConnectionAsync(actor, organization.Id, workspace.Id, connection.Id)).Environment);
    }

    [Fact]
    public async Task BulkEditCanPatchEveryOperationalMetadataFamilyWithoutReplacingOtherValues()
    {
        await using var fixture = new ServiceFixture();
        var (organization, workspace) = await fixture.CreateScopeAsync();
        var actor = ServiceFixture.Actor(organization.Id, WorkspaceRole.WorkspaceAdministrator);
        var connection = await fixture.Service.SaveConnectionAsync(actor, organization.Id, workspace.Id, null, Connection("Metadata target", ResourceVisibility.Shared));
        var patch = new ConnectionMetadataPatchDto(
            BusinessUnit: "Finance", ServiceOwner: "Platform", Geography: "HU", DataClassification: "Restricted",
            MaintenanceWindow: "Saturday", SupportGroup: "DBA", EscalationContact: "On call", AssetId: "asset-2",
            CmdbId: "cmdb-2", OperatingSystem: "Windows Server 2025", MonitoringUrl: "https://monitor.example/asset-2",
            DocumentationUrl: "https://docs.example/asset-2", TicketingUrl: "https://tickets.example/asset-2");

        var result = await fixture.Service.BulkEditConnectionsAsync(actor, organization.Id, workspace.Id, "metadata-bulk-0001",
            new BulkConnectionEditRequest([connection.Id], null, null, false, [], [], "App team", "Critical", patch));
        var updated = await fixture.Service.GetConnectionAsync(actor, organization.Id, workspace.Id, connection.Id);

        Assert.True(Assert.Single(result.Items).Succeeded);
        Assert.Equal("Finance", updated.Metadata.BusinessUnit);
        Assert.Equal("App team", updated.Metadata.ApplicationOwner);
        Assert.Equal("Platform", updated.Metadata.ServiceOwner);
        Assert.Equal("Critical", updated.Metadata.Criticality);
        Assert.Equal("HU", updated.Metadata.Geography);
        Assert.Equal("Restricted", updated.Metadata.DataClassification);
        Assert.Equal("Saturday", updated.Metadata.MaintenanceWindow);
        Assert.Equal("DBA", updated.Metadata.SupportGroup);
        Assert.Equal("On call", updated.Metadata.EscalationContact);
        Assert.Equal("asset-2", updated.Metadata.AssetId);
        Assert.Equal("cmdb-2", updated.Metadata.CmdbId);
        Assert.Equal("Windows Server 2025", updated.Metadata.OperatingSystem);
        Assert.Equal("https://monitor.example/asset-2", updated.Metadata.MonitoringUrl);
        Assert.Equal("https://docs.example/asset-2", updated.Metadata.DocumentationUrl);
        Assert.Equal("https://tickets.example/asset-2", updated.Metadata.TicketingUrl);
    }

    [Fact]
    public async Task DynamicGroupsTemplatesAndPersonalStateAreSecretFreeAndScoped()
    {
        await using var fixture = new ServiceFixture();
        var (organization, workspace) = await fixture.CreateScopeAsync();
        var actor = ServiceFixture.Actor(organization.Id, WorkspaceRole.WorkspaceAdministrator);
        var source = await fixture.Service.SaveConnectionAsync(actor, organization.Id, workspace.Id, null, Connection("Template source", ResourceVisibility.Shared) with
        {
            Metadata = new ConnectionMetadataDto("IT", "App owner", "Service owner", "medium", "EU", "internal", "Sun 02:00", "Support", "Escalation",
                "asset-1", "cmdb-1", "Windows Server", "https://monitor.example/asset-1", "https://docs.example/asset-1", "https://tickets.example/asset-1")
        });
        var group = await fixture.Service.SaveDynamicGroupAsync(actor, organization.Id, workspace.Id, null,
            new SaveDynamicGroupRequest("Operational metadata", [
                new DynamicGroupRuleDto("tag", "equals", "managed"),
                new DynamicGroupRuleDto("businessUnit", "equals", "IT"),
                new DynamicGroupRuleDto("applicationOwner", "equals", "App owner"),
                new DynamicGroupRuleDto("serviceOwner", "equals", "Service owner"),
                new DynamicGroupRuleDto("criticality", "equals", "medium"),
                new DynamicGroupRuleDto("geography", "equals", "EU"),
                new DynamicGroupRuleDto("dataClassification", "equals", "internal"),
                new DynamicGroupRuleDto("maintenanceWindow", "contains", "02:00"),
                new DynamicGroupRuleDto("supportGroup", "equals", "Support"),
                new DynamicGroupRuleDto("escalationContact", "equals", "Escalation"),
                new DynamicGroupRuleDto("assetId", "equals", "asset-1"),
                new DynamicGroupRuleDto("cmdbId", "equals", "cmdb-1"),
                new DynamicGroupRuleDto("operatingSystem", "contains", "Server"),
                new DynamicGroupRuleDto("monitoringUrl", "contains", "monitor.example"),
                new DynamicGroupRuleDto("documentationUrl", "contains", "docs.example"),
                new DynamicGroupRuleDto("ticketingUrl", "contains", "tickets.example")
            ], null));
        var defaults = new TemplateProfileDefaults(source.Environment, source.Notes, source.Tags, source.Metadata, source.Settings);
        var template = await fixture.Service.SaveConnectionTemplateAsync(actor, organization.Id, workspace.Id, null,
            new SaveConnectionTemplateRequest("Safe baseline", "No target or secret", defaults, null));
        var created = await fixture.Service.CreateConnectionFromTemplateAsync(actor, organization.Id, workspace.Id,
            new CreateConnectionFromTemplateRequest(template.Id, null, ResourceVisibility.Personal, "From template", "new.example.test", 3389, "DOMAIN\\operator"));
        var state = await fixture.Service.SaveUserWorkspaceStateAsync(actor, organization.Id, workspace.Id,
            new SaveUserWorkspaceStateRequest([], new LayoutPreferencesDto(320, "name", "system", true), [new SavedWorkspaceDto(Guid.CreateVersion7(), "Daily", [created.Id], SessionDisplayMode.Embedded)], [], null));

        Assert.Contains(source.Id, group.MatchingConnectionIds);
        Assert.Equal("new.example.test", created.Host);
        Assert.Equal(actor.PrincipalId, created.OwnerUserId);
        Assert.Single(state.SavedWorkspaces);
        Assert.DoesNotContain("password", JsonSerializer.Serialize(new { group, template, created, state }), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PersonalWorkspaceStateRemovesDeletedTargetsButKeepsSafeDescriptors()
    {
        await using var fixture = new ServiceFixture();
        var (organization, workspace) = await fixture.CreateScopeAsync();
        var actor = ServiceFixture.Actor(organization.Id, WorkspaceRole.WorkspaceAdministrator);
        var folder = await fixture.Service.SaveFolderAsync(actor, organization.Id, workspace.Id, null, new UpsertFolderRequest(null, "Daily", 0, null));
        var connection = await fixture.Service.SaveConnectionAsync(actor, organization.Id, workspace.Id, null,
            Connection("Disposable", ResourceVisibility.Personal) with { FolderId = folder.Id });
        var savedWorkspaceId = Guid.CreateVersion7();
        var tabGroupId = Guid.CreateVersion7();

        await fixture.Service.SaveUserWorkspaceStateAsync(actor, organization.Id, workspace.Id,
            new SaveUserWorkspaceStateRequest([folder.Id], new LayoutPreferencesDto(320, "name", "system", true),
                [new SavedWorkspaceDto(savedWorkspaceId, "Daily", [connection.Id], SessionDisplayMode.Embedded)],
                [new TabGroupDto(tabGroupId, "Operations", "#5B8DEF", [connection.Id])], null));
        await fixture.Service.DeleteConnectionAsync(actor, organization.Id, workspace.Id, connection.Id, connection.Revision);
        fixture.Context.Folders.Remove(await fixture.Context.Folders.SingleAsync(item => item.Id == folder.Id));
        await fixture.Context.SaveChangesAsync();

        var sanitized = await fixture.Service.GetUserWorkspaceStateAsync(actor, organization.Id, workspace.Id);

        Assert.Empty(sanitized.PinnedFolderIds);
        Assert.Equal(savedWorkspaceId, Assert.Single(sanitized.SavedWorkspaces).Id);
        Assert.Empty(sanitized.SavedWorkspaces[0].ConnectionIds);
        Assert.Equal(tabGroupId, Assert.Single(sanitized.TabGroups).Id);
        Assert.Empty(sanitized.TabGroups[0].ConnectionIds);
        Assert.True(sanitized.Layout.ReopenLastWorkspace);
    }

    [Fact]
    public async Task GlobalLogoutRevokesProductSessionsWithoutRemovingMemberships()
    {
        await using var fixture = new ServiceFixture();
        var (organization, workspace) = await fixture.CreateScopeAsync();
        var actor = ServiceFixture.Actor(organization.Id, WorkspaceRole.WorkspaceAdministrator);
        var user = new PlatformUser { Id = actor.PrincipalId, Issuer = "issuer", Subject = actor.PrincipalId.ToString("N"), DisplayName = "User", Status = MembershipStatus.Active, SessionVersion = 1 };
        fixture.Context.Users.Add(user);
        fixture.Context.Memberships.Add(new WorkspaceMembership { OrganizationId = organization.Id, WorkspaceId = workspace.Id, UserId = user.Id, Role = WorkspaceRole.Operator, Status = MembershipStatus.Active });
        fixture.Context.ProductSessions.Add(new ProductSession { UserId = user.Id, SessionVersion = 1, StartedAtUtc = DateTimeOffset.UtcNow, LastActivityAtUtc = DateTimeOffset.UtcNow, IdleExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30), AbsoluteExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(8), IdleTimeoutMinutes = 30, ClientId = "nextrdp-desktop" });
        await fixture.Context.SaveChangesAsync();
        var authorization = new AllowAuthorization();
        var sessions = new PlatformSessionRevocationService(fixture.Context, authorization, authorization, TimeProvider.System);

        await sessions.RevokeSelfAsync(actor);

        Assert.Equal(2, user.SessionVersion);
        Assert.NotNull((await fixture.Context.ProductSessions.SingleAsync()).RevokedAtUtc);
        Assert.Equal(MembershipStatus.Active, (await fixture.Context.Memberships.SingleAsync()).Status);
    }

    private static UpsertManagedConnectionRequest Connection(string name, ResourceVisibility visibility) =>
        new(
            null,
            visibility,
            name,
            $"{name.Replace(' ', '-').ToLowerInvariant()}.example.test",
            3389,
            string.Empty,
            ConnectionEnvironment.Development,
            string.Empty,
            ["managed"],
            new ConnectionMetadataDto("IT", "App owner", "Service owner", "medium", "EU", "internal", "Sun 02:00", "Support", "Escalation", "asset-1", "cmdb-1", "Windows Server", string.Empty, string.Empty, string.Empty),
            new ManagedRdpSettingsDto(ManagedConnectionKind.Desktop, true, false, string.Empty, GatewayUsage.Never, string.Empty, 443, string.Empty, string.Empty, string.Empty,
                SessionDisplayMode.Embedded, [], null, null, 32, PerformancePreset.Auto, true, ThumbnailMode.Disabled, ShortcutRouting.RemoteInFullScreen, true,
                false, false, false, false, false, false, false, false, true, TransferDirection.Bidirectional, TransferDirection.Disabled, 0, 5),
            null);

    private sealed class ServiceFixture : IAsyncDisposable
    {
        private readonly AllowAuthorization authorization = new();

        public ServiceFixture(bool enableRelationships = false)
        {
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
            Context = new PlatformDbContext(options);
            CredentialStore = new RecordingCredentialStore();
            var codec = new ProtectedSyncCursorCodec(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
            Relationship = new RecordingRelationshipService();
            Service = new PlatformManagementService(Context, authorization, authorization, codec, CredentialStore, TimeProvider.System,
                enableRelationships ? Relationship : null);
        }

        public PlatformDbContext Context { get; }
        public PlatformManagementService Service { get; }
        public RecordingCredentialStore CredentialStore { get; }
        public RecordingRelationshipService Relationship { get; }

        public static PlatformActor Actor(Guid organizationId, WorkspaceRole role) =>
            new(Guid.CreateVersion7(), $"issuer|{Guid.CreateVersion7():N}", organizationId, new HashSet<WorkspaceRole> { role }, [], true, 1);

        public async Task<(OrganizationDto Organization, WorkspaceDto Workspace)> CreateScopeAsync()
        {
            var platformActor = new PlatformActor(Guid.CreateVersion7(), "issuer|platform", null, new HashSet<WorkspaceRole> { WorkspaceRole.PlatformAdministrator }, [], true, 1);
            var organization = await Service.CreateOrganizationAsync(platformActor, new CreateOrganizationRequest("Example", $"example-{Guid.NewGuid():N}"[..24]));
            var workspace = await Service.CreateWorkspaceAsync(platformActor, organization.Id, new CreateWorkspaceRequest("Operations", "operations", "Test workspace"));
            return (organization, workspace);
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class AllowAuthorization : IResourceAuthorizationService, IAttributePolicyService
    {
        public Task<AuthorizationDecision> AuthorizeAsync(PlatformActor actor, PlatformAction action, AuthorizationTarget target, CancellationToken cancellationToken = default) =>
            Task.FromResult(AuthorizationDecision.Allow("test"));

        public Task<AuthorizationDecision> EvaluateAsync(PlatformActor actor, PlatformAction action, AuthorizationTarget target, IReadOnlyDictionary<string, object?> attributes, CancellationToken cancellationToken = default) =>
            Task.FromResult(AuthorizationDecision.Allow("test"));
    }

    public sealed class RecordingCredentialStore : IServiceAccountCredentialStore
    {
        public string? LastSecret { get; private set; }

        public Task StoreAsync(string clientId, string clientSecret, IReadOnlyList<string> scopes, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken = default)
        {
            LastSecret = clientSecret;
            return Task.CompletedTask;
        }

        public Task RotateAsync(string clientId, string clientSecret, IReadOnlyList<string> scopes, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken = default)
        {
            LastSecret = clientSecret;
            return Task.CompletedTask;
        }

        public Task RevokeAsync(string clientId, CancellationToken cancellationToken = default)
        {
            LastSecret = null;
            return Task.CompletedTask;
        }
    }

    public sealed class RecordingRelationshipService : IResourceRelationshipService
    {
        public List<(Guid PrincipalId, WorkspaceRole Role, AuthorizationTarget Target)> Ensured { get; } = [];
        public List<(Guid PrincipalId, WorkspaceRole Role, AuthorizationTarget Target)> Removed { get; } = [];
        public Guid? LastEnsuredPrincipalId { get; private set; }
        public bool LastEnsuredWasServiceAccount { get; private set; }
        public AuthorizationTarget? LastEnsuredTarget { get; private set; }
        public Guid? LastRemovedPrincipalId { get; private set; }
        public bool LastRemovedWasServiceAccount { get; private set; }

        public Task EnsureRoleAsync(Guid principalId, bool isServiceAccount, WorkspaceRole role, AuthorizationTarget target, CancellationToken cancellationToken = default)
        {
            LastEnsuredPrincipalId = principalId;
            LastEnsuredWasServiceAccount = isServiceAccount;
            LastEnsuredTarget = target;
            Ensured.Add((principalId, role, target));
            return Task.CompletedTask;
        }

        public Task RemoveRoleAsync(Guid principalId, bool isServiceAccount, WorkspaceRole role, AuthorizationTarget target, CancellationToken cancellationToken = default)
        {
            LastRemovedPrincipalId = principalId;
            LastRemovedWasServiceAccount = isServiceAccount;
            Removed.Add((principalId, role, target));
            return Task.CompletedTask;
        }
    }
}
