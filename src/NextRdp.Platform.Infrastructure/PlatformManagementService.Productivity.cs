using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Infrastructure;

public sealed partial class PlatformManagementService
{
    private static readonly HashSet<string> DynamicGroupFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "host", "tag", "environment", "businessUnit", "applicationOwner", "serviceOwner", "criticality",
        "geography", "dataClassification", "maintenanceWindow", "supportGroup", "escalationContact", "assetId",
        "cmdbId", "operatingSystem", "monitoringUrl", "documentationUrl", "ticketingUrl"
    };
    private static readonly HashSet<string> DynamicGroupOperators = new(StringComparer.OrdinalIgnoreCase) { "equals", "contains" };

    public async Task<IReadOnlyList<DynamicGroupDto>> ListDynamicGroupsAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ReadInventory, organizationId, workspaceId, "workspace", workspaceId, cancellationToken: cancellationToken);
        var groups = await dbContext.DynamicGroups.AsNoTracking().Where(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId)
            .OrderBy(entity => entity.Name).ThenBy(entity => entity.Id).ToListAsync(cancellationToken);
        var connections = await PermittedConnectionsAsync(actor, organizationId, workspaceId, cancellationToken);
        return groups.Select(group => ToDynamicGroupDto(group, connections)).ToArray();
    }

    public async Task<DynamicGroupDto> SaveDynamicGroupAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, Guid? groupId, SaveDynamicGroupRequest request, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ManageSharedInventory, organizationId, workspaceId, groupId is null ? "workspace" : "dynamicGroup", groupId ?? workspaceId, cancellationToken: cancellationToken);
        await EnsureWorkspaceExistsAsync(organizationId, workspaceId, cancellationToken);
        var rules = NormalizeDynamicRules(request.Rules);
        var now = timeProvider.GetUtcNow();
        DynamicConnectionGroup group;
        var created = groupId is null;
        if (created)
        {
            group = new DynamicConnectionGroup { OrganizationId = organizationId, WorkspaceId = workspaceId, CreatedAtUtc = now };
            dbContext.DynamicGroups.Add(group);
        }
        else
        {
            group = await dbContext.DynamicGroups.SingleOrDefaultAsync(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && entity.Id == groupId, cancellationToken)
                ?? throw PlatformProblemException.NotFound("dynamic group");
            RequireRevision(request.ExpectedRevision, group.Revision);
            group.Revision++;
        }
        group.Name = request.Name.Trim();
        group.RulesJson = JsonSerializer.Serialize(rules, JsonOptions);
        group.UpdatedAtUtc = now;
        Validate(group.Validate());
        var connections = await PermittedConnectionsAsync(actor, organizationId, workspaceId, cancellationToken);
        var dto = ToDynamicGroupDto(group, connections);
        RecordSync(organizationId, workspaceId, null, "dynamicGroup", group.Id, SyncChangeKind.Upsert, group.Revision, dto);
        RecordAudit(actor, organizationId, workspaceId, created ? "dynamic_group.created" : "dynamic_group.updated", "dynamicGroup", group.Id, AuditOutcome.Succeeded,
            new { ruleCount = rules.Length, matchCount = dto.MatchingConnectionIds.Count, group.Revision });
        await SaveChangesAsync(cancellationToken);
        return dto;
    }

    public async Task DeleteDynamicGroupAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, Guid groupId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ManageSharedInventory, organizationId, workspaceId, "dynamicGroup", groupId, cancellationToken: cancellationToken);
        var group = await dbContext.DynamicGroups.SingleOrDefaultAsync(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && entity.Id == groupId, cancellationToken)
            ?? throw PlatformProblemException.NotFound("dynamic group");
        RequireRevision(expectedRevision, group.Revision);
        dbContext.DynamicGroups.Remove(group);
        RecordSync(organizationId, workspaceId, null, "dynamicGroup", group.Id, SyncChangeKind.Delete, group.Revision + 1, null);
        RecordAudit(actor, organizationId, workspaceId, "dynamic_group.deleted", "dynamicGroup", group.Id, AuditOutcome.Succeeded, new { revision = group.Revision + 1 });
        await SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConnectionTemplateDto>> ListConnectionTemplatesAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ReadInventory, organizationId, workspaceId, "workspace", workspaceId, cancellationToken: cancellationToken);
        var entities = await dbContext.ConnectionTemplates.AsNoTracking().Where(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId)
            .OrderBy(entity => entity.Name).ThenBy(entity => entity.Id).ToListAsync(cancellationToken);
        return entities.Select(ToConnectionTemplateDto).ToArray();
    }

    public async Task<ConnectionTemplateDto> SaveConnectionTemplateAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, Guid? templateId, SaveConnectionTemplateRequest request, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ManageSharedInventory, organizationId, workspaceId, templateId is null ? "workspace" : "connectionTemplate", templateId ?? workspaceId, cancellationToken: cancellationToken);
        ValidateTemplateDefaults(request.Defaults);
        var now = timeProvider.GetUtcNow();
        ManagedConnectionTemplate entity;
        var created = templateId is null;
        if (created)
        {
            entity = new ManagedConnectionTemplate { OrganizationId = organizationId, WorkspaceId = workspaceId, CreatedAtUtc = now };
            dbContext.ConnectionTemplates.Add(entity);
        }
        else
        {
            entity = await dbContext.ConnectionTemplates.SingleOrDefaultAsync(item => item.OrganizationId == organizationId && item.WorkspaceId == workspaceId && item.Id == templateId, cancellationToken)
                ?? throw PlatformProblemException.NotFound("connection template");
            RequireRevision(request.ExpectedRevision, entity.Revision);
            entity.Revision++;
        }
        entity.Name = request.Name.Trim();
        entity.Description = request.Description.Trim();
        entity.TemplateJson = JsonSerializer.Serialize(request.Defaults, JsonOptions);
        entity.UpdatedAtUtc = now;
        Validate(entity.Validate());
        var dto = ToConnectionTemplateDto(entity);
        RecordSync(organizationId, workspaceId, null, "connectionTemplate", entity.Id, SyncChangeKind.Upsert, entity.Revision, dto);
        RecordAudit(actor, organizationId, workspaceId, created ? "connection_template.created" : "connection_template.updated", "connectionTemplate", entity.Id, AuditOutcome.Succeeded,
            new { entity.Revision });
        await SaveChangesAsync(cancellationToken);
        return dto;
    }

    public async Task<ManagedConnectionDto> CreateConnectionFromTemplateAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, CreateConnectionFromTemplateRequest request, CancellationToken cancellationToken = default)
    {
        var template = await dbContext.ConnectionTemplates.AsNoTracking().SingleOrDefaultAsync(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && entity.Id == request.TemplateId, cancellationToken)
            ?? throw PlatformProblemException.NotFound("connection template");
        var defaults = JsonSerializer.Deserialize<TemplateProfileDefaults>(template.TemplateJson, JsonOptions)
            ?? throw new InvalidOperationException("Stored connection template is invalid.");
        var saved = await SaveConnectionAsync(actor, organizationId, workspaceId, null,
            new UpsertManagedConnectionRequest(request.FolderId, request.Visibility, request.Name, request.Host, request.Port, request.UsernameHint,
                defaults.Environment, defaults.Notes, defaults.Tags, defaults.Metadata, defaults.Settings, null), cancellationToken);
        RecordAudit(actor, organizationId, workspaceId, "connection.created_from_template", "connection", saved.Id, AuditOutcome.Succeeded, new { templateId = template.Id });
        await SaveChangesAsync(cancellationToken);
        return saved;
    }

    public async Task<ManagedConnectionDto> CopyConnectionAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, Guid connectionId, CopyManagedConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var source = await GetConnectionAsync(actor, organizationId, workspaceId, connectionId, cancellationToken);
        var copy = await SaveConnectionAsync(actor, organizationId, workspaceId, null,
            new UpsertManagedConnectionRequest(request.FolderId, request.Visibility, request.Name, source.Host, source.Port, source.UsernameHint,
                source.Environment, source.Notes, source.Tags.ToArray(), source.Metadata, source.Settings, null), cancellationToken);
        RecordAudit(actor, organizationId, workspaceId, "connection.copied_without_secrets", "connection", copy.Id, AuditOutcome.Succeeded, new { sourceId = source.Id });
        await SaveChangesAsync(cancellationToken);
        return copy;
    }

    public async Task<UserWorkspaceStateDto> GetUserWorkspaceStateAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ReadInventory, organizationId, workspaceId, "workspace", workspaceId, cancellationToken: cancellationToken);
        var state = await dbContext.UserWorkspaceStates.AsNoTracking().SingleOrDefaultAsync(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && entity.UserId == actor.PrincipalId, cancellationToken);
        return await ToSanitizedUserStateAsync(state, actor, organizationId, workspaceId, cancellationToken);
    }

    public async Task<UserWorkspaceStateDto> SaveUserWorkspaceStateAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, SaveUserWorkspaceStateRequest request, CancellationToken cancellationToken = default)
    {
        if (actor.IsServiceAccount) throw PlatformProblemException.Forbidden("Service accounts cannot own personal workspace layout state.");
        await DemandAsync(actor, PlatformAction.ManageProductivity, organizationId, workspaceId, "workspace", workspaceId, cancellationToken: cancellationToken);
        ValidateWorkspaceState(request);
        var existing = await dbContext.UserWorkspaceStates.SingleOrDefaultAsync(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && entity.UserId == actor.PrincipalId, cancellationToken);
        if (existing is null)
        {
            if (request.ExpectedRevision is not null) throw PlatformProblemException.Conflict("Workspace state does not exist yet; omit the expected revision.");
            existing = new UserWorkspaceState { OrganizationId = organizationId, WorkspaceId = workspaceId, UserId = actor.PrincipalId };
            dbContext.UserWorkspaceStates.Add(existing);
        }
        else
        {
            RequireRevision(request.ExpectedRevision, existing.Revision);
            existing.Revision++;
        }
        existing.PinnedFolderIdsJson = JsonSerializer.Serialize(request.PinnedFolderIds.Distinct().ToArray(), JsonOptions);
        existing.LayoutJson = JsonSerializer.Serialize(request.Layout, JsonOptions);
        existing.SavedWorkspacesJson = JsonSerializer.Serialize(request.SavedWorkspaces, JsonOptions);
        existing.TabGroupsJson = JsonSerializer.Serialize(request.TabGroups, JsonOptions);
        existing.UpdatedAtUtc = timeProvider.GetUtcNow();
        var dto = await ToSanitizedUserStateAsync(existing, actor, organizationId, workspaceId, cancellationToken);
        RecordSync(organizationId, workspaceId, actor.PrincipalId, "userWorkspaceState", actor.PrincipalId, SyncChangeKind.Upsert, existing.Revision, dto);
        RecordAudit(actor, organizationId, workspaceId, "workspace_state.updated", "userWorkspaceState", actor.PrincipalId, AuditOutcome.Succeeded,
            new { pins = dto.PinnedFolderIds.Count, savedWorkspaces = dto.SavedWorkspaces.Count, tabGroups = dto.TabGroups.Count, existing.Revision });
        await SaveChangesAsync(cancellationToken);
        return dto;
    }

    public async Task<BulkProvisionResult> BulkEditConnectionsAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, string idempotencyKey, BulkConnectionEditRequest request, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.RunBulkAutomation, organizationId, workspaceId, "workspace", workspaceId, cancellationToken: cancellationToken);
        if (request.ConnectionIds.Count is < 1 or > 100 || request.ConnectionIds.Distinct().Count() != request.ConnectionIds.Count)
            throw PlatformProblemException.Validation(["Bulk edit requires between 1 and 100 unique connection IDs."]);
        if (request.ClearFolder && request.FolderId is not null) throw PlatformProblemException.Validation(["Bulk edit cannot set and clear the folder together."]);
        if (request.FolderId is not null && !await dbContext.Folders.AnyAsync(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && entity.Id == request.FolderId, cancellationToken))
            throw PlatformProblemException.NotFound("folder");
        if (request.AddTags.Count > 50 || request.RemoveTags.Count > 50) throw PlatformProblemException.Validation(["Bulk tag changes cannot exceed 50 additions or removals."]);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200 || idempotencyKey.Any(char.IsControl))
            throw PlatformProblemException.Validation(["Idempotency key is required, cannot exceed 200 characters, and cannot contain control characters."]);
        var actorKey = actor.PrincipalKey.Length <= 300 ? actor.PrincipalKey : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actor.PrincipalKey)));
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestJson)));
        var existingIdempotency = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(entity =>
            entity.OrganizationId == organizationId && entity.ActorKey == actorKey && entity.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existingIdempotency is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(existingIdempotency.RequestHash), Convert.FromHexString(requestHash)))
                throw PlatformProblemException.Conflict("The idempotency key was already used with a different request.");
            var replay = JsonSerializer.Deserialize<BulkProvisionResult>(existingIdempotency.ResponseJson, JsonOptions)
                ?? throw new InvalidOperationException("Stored idempotency response is invalid.");
            return replay with { Replayed = true };
        }
        var results = new List<BulkItemResult>();
        foreach (var id in request.ConnectionIds)
        {
            try
            {
                var current = await GetConnectionAsync(actor, organizationId, workspaceId, id, cancellationToken);
                var tags = current.Tags.Concat(request.AddTags).Where(tag => !request.RemoveTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                var metadata = current.Metadata with
                {
                    BusinessUnit = request.Metadata?.BusinessUnit?.Trim() ?? current.Metadata.BusinessUnit,
                    ApplicationOwner = request.ApplicationOwner?.Trim() ?? request.Metadata?.ApplicationOwner?.Trim() ?? current.Metadata.ApplicationOwner,
                    ServiceOwner = request.Metadata?.ServiceOwner?.Trim() ?? current.Metadata.ServiceOwner,
                    Criticality = request.Criticality?.Trim() ?? request.Metadata?.Criticality?.Trim() ?? current.Metadata.Criticality,
                    Geography = request.Metadata?.Geography?.Trim() ?? current.Metadata.Geography,
                    DataClassification = request.Metadata?.DataClassification?.Trim() ?? current.Metadata.DataClassification,
                    MaintenanceWindow = request.Metadata?.MaintenanceWindow?.Trim() ?? current.Metadata.MaintenanceWindow,
                    SupportGroup = request.Metadata?.SupportGroup?.Trim() ?? current.Metadata.SupportGroup,
                    EscalationContact = request.Metadata?.EscalationContact?.Trim() ?? current.Metadata.EscalationContact,
                    AssetId = request.Metadata?.AssetId?.Trim() ?? current.Metadata.AssetId,
                    CmdbId = request.Metadata?.CmdbId?.Trim() ?? current.Metadata.CmdbId,
                    OperatingSystem = request.Metadata?.OperatingSystem?.Trim() ?? current.Metadata.OperatingSystem,
                    MonitoringUrl = request.Metadata?.MonitoringUrl?.Trim() ?? current.Metadata.MonitoringUrl,
                    DocumentationUrl = request.Metadata?.DocumentationUrl?.Trim() ?? current.Metadata.DocumentationUrl,
                    TicketingUrl = request.Metadata?.TicketingUrl?.Trim() ?? current.Metadata.TicketingUrl
                };
                var saved = await SaveConnectionAsync(actor, organizationId, workspaceId, id,
                    new UpsertManagedConnectionRequest(request.ClearFolder ? null : request.FolderId ?? current.FolderId, current.Visibility, current.Name, current.Host,
                        current.Port, current.UsernameHint, request.Environment ?? current.Environment, current.Notes, tags, metadata, current.Settings, current.Revision), cancellationToken);
                results.Add(new BulkItemResult(id.ToString("D"), saved.Id, true, "ok", "Updated."));
            }
            catch (PlatformProblemException exception)
            {
                dbContext.ChangeTracker.Clear();
                results.Add(new BulkItemResult(id.ToString("D"), id, false, exception.Code, exception.Message));
            }
        }
        var result = new BulkProvisionResult(results, false);
        dbContext.IdempotencyRecords.Add(new IdempotencyRecord
        {
            OrganizationId = organizationId,
            ActorKey = actorKey,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            StatusCode = results.All(item => item.Succeeded) ? 200 : 207,
            ResponseJson = JsonSerializer.Serialize(result, JsonOptions),
            CreatedAtUtc = timeProvider.GetUtcNow(),
            ExpiresAtUtc = timeProvider.GetUtcNow().AddHours(24)
        });
        RecordAudit(actor, organizationId, workspaceId, "connections.bulk_edited", "workspace", workspaceId, AuditOutcome.Succeeded,
            new { requested = request.ConnectionIds.Count, succeeded = results.Count(item => item.Succeeded), failed = results.Count(item => !item.Succeeded), idempotencyKeyHash = requestHash });
        await SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<BulkConnectPreviewResult> PreviewBulkConnectAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, BulkConnectPreviewRequest request, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.Connect, organizationId, workspaceId, "workspace", workspaceId, cancellationToken: cancellationToken);
        if (request.ConnectionIds.Count is < 1 or > 100 || request.ConnectionIds.Distinct().Count() != request.ConnectionIds.Count)
            throw PlatformProblemException.Validation(["Bulk connect preview requires between 1 and 100 unique connection IDs."]);
        var effective = await GetEffectivePolicyAsync(actor, organizationId, workspaceId, null, null, cancellationToken);
        var maximum = 10;
        if (effective.Fields.TryGetValue("client.bulkConnectLimit", out var field) && int.TryParse(field.JsonValue, out var configured)) maximum = Math.Clamp(configured, 1, 25);
        var permitted = await PermittedConnectionsAsync(actor, organizationId, workspaceId, cancellationToken);
        var allowed = request.ConnectionIds.Where(id => permitted.Any(connection => connection.Id == id)).Take(maximum).ToArray();
        var denied = request.ConnectionIds.Except(allowed).ToArray();
        var warnings = new List<string> { $"Opening {allowed.Length} sessions will prompt separately for Windows credentials." };
        if (request.ConnectionIds.Count > maximum) warnings.Add($"The effective bulk-connect limit is {maximum}; excess selections were denied.");
        if (allowed.Any(id => permitted.Single(connection => connection.Id == id).Environment == NextRdp.Core.ConnectionEnvironment.Production))
            warnings.Add("The selection includes one or more Production connections.");
        RecordAudit(actor, organizationId, workspaceId, "connections.bulk_connect_previewed", "workspace", workspaceId, AuditOutcome.Succeeded,
            new { requested = request.ConnectionIds.Count, allowed = allowed.Length, denied = denied.Length, maximum });
        await SaveChangesAsync(cancellationToken);
        return new BulkConnectPreviewResult(allowed, denied, maximum, warnings);
    }

    public async Task<GatewayDiagnosticResult> DiagnoseGatewayAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, GatewayDiagnosticRequest request, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.RunGatewayDiagnostics, organizationId, workspaceId, "workspace", workspaceId,
            new Dictionary<string, object?> { ["requireTls"] = request.RequireTls }, cancellationToken);
        if (!PlatformValidation.IsValidHost(request.Host) || request.Port is < 1 or > 65535)
            throw PlatformProblemException.Validation(["Gateway host and port are invalid."]);
        var serverName = string.IsNullOrWhiteSpace(request.ExpectedServerName) ? request.Host : request.ExpectedServerName.Trim();
        if (!PlatformValidation.IsValidHost(serverName)) throw PlatformProblemException.Validation(["Expected TLS server name is invalid."]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var watch = Stopwatch.StartNew();
        var dns = false; var addressCount = 0; var tcp = false; var tls = false; var outcome = "dns_failed";
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(request.Host, timeout.Token);
            dns = addresses.Length > 0; addressCount = addresses.Length;
            if (!dns) throw new SocketException((int)SocketError.HostNotFound);
            using var client = new TcpClient();
            await client.ConnectAsync(request.Host, request.Port, timeout.Token);
            tcp = true; outcome = request.RequireTls ? "tls_failed" : "reachable";
            if (request.RequireTls)
            {
                using var stream = new SslStream(client.GetStream(), false);
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = serverName,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.Online
                }, timeout.Token);
                tls = stream.IsAuthenticated && stream.IsEncrypted;
                outcome = tls ? "reachable_tls_valid" : "tls_failed";
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { outcome = "timeout"; }
        catch (SocketException) { outcome = dns ? "tcp_failed" : "dns_failed"; }
        catch (AuthenticationException) { outcome = "tls_validation_failed"; }
        catch (IOException) { outcome = tcp ? "tls_failed" : "tcp_failed"; }
        watch.Stop();
        var result = new GatewayDiagnosticResult(dns, addressCount, tcp, tls, outcome, watch.ElapsedMilliseconds, timeProvider.GetUtcNow());
        RecordAudit(actor, organizationId, workspaceId, "gateway.diagnostic_executed", "workspace", workspaceId,
            outcome.StartsWith("reachable", StringComparison.Ordinal) ? AuditOutcome.Succeeded : AuditOutcome.Failed,
            new { request.Port, request.RequireTls, result.DnsSucceeded, result.AddressCount, result.TcpSucceeded, result.TlsSucceeded, result.Outcome, result.DurationMilliseconds });
        await SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task<List<ManagedConnection>> PermittedConnectionsAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, CancellationToken cancellationToken) =>
        await dbContext.Connections.AsNoTracking().Where(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId &&
            (entity.Visibility == ResourceVisibility.Shared || entity.OwnerUserId == actor.PrincipalId)).ToListAsync(cancellationToken);

    private static DynamicGroupRuleDto[] NormalizeDynamicRules(IReadOnlyList<DynamicGroupRuleDto> rules)
    {
        if (rules.Count is < 1 or > 20) throw PlatformProblemException.Validation(["A dynamic group requires between 1 and 20 rules."]);
        var normalized = rules.Select(rule => new DynamicGroupRuleDto(rule.Field.Trim(), rule.Operator.Trim(), rule.Value.Trim())).ToArray();
        if (normalized.Any(rule => !DynamicGroupFields.Contains(rule.Field) || !DynamicGroupOperators.Contains(rule.Operator) || string.IsNullOrWhiteSpace(rule.Value) || rule.Value.Length > 512 || rule.Value.Any(char.IsControl)))
            throw PlatformProblemException.Validation(["Dynamic group rules contain an unsupported field, operator, or value."]);
        return normalized;
    }

    private static DynamicGroupDto ToDynamicGroupDto(DynamicConnectionGroup group, IReadOnlyList<ManagedConnection> connections)
    {
        var rules = JsonSerializer.Deserialize<DynamicGroupRuleDto[]>(group.RulesJson, JsonOptions) ?? [];
        var matches = connections.Where(connection => rules.All(rule => MatchesRule(connection, rule))).Select(connection => connection.Id).Order().ToArray();
        return new DynamicGroupDto(group.Id, group.OrganizationId, group.WorkspaceId, group.Name, rules, matches, group.Revision, group.UpdatedAtUtc);
    }

    private static bool MatchesRule(ManagedConnection connection, DynamicGroupRuleDto rule)
    {
        IEnumerable<string> values = rule.Field.ToLowerInvariant() switch
        {
            "name" => [connection.Name],
            "host" => [connection.Host],
            "tag" => connection.Tags,
            "environment" => [connection.Environment.ToString()],
            "businessunit" => [connection.Metadata.BusinessUnit],
            "applicationowner" => [connection.Metadata.ApplicationOwner],
            "serviceowner" => [connection.Metadata.ServiceOwner],
            "criticality" => [connection.Metadata.Criticality],
            "geography" => [connection.Metadata.Geography],
            "dataclassification" => [connection.Metadata.DataClassification],
            "maintenancewindow" => [connection.Metadata.MaintenanceWindow],
            "supportgroup" => [connection.Metadata.SupportGroup],
            "escalationcontact" => [connection.Metadata.EscalationContact],
            "assetid" => [connection.Metadata.AssetId],
            "cmdbid" => [connection.Metadata.CmdbId],
            "operatingsystem" => [connection.Metadata.OperatingSystem],
            "monitoringurl" => [connection.Metadata.MonitoringUrl],
            "documentationurl" => [connection.Metadata.DocumentationUrl],
            "ticketingurl" => [connection.Metadata.TicketingUrl],
            _ => []
        };
        return rule.Operator.Equals("equals", StringComparison.OrdinalIgnoreCase)
            ? values.Any(value => value.Equals(rule.Value, StringComparison.OrdinalIgnoreCase))
            : values.Any(value => value.Contains(rule.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateTemplateDefaults(TemplateProfileDefaults defaults)
    {
        var validation = new ManagedConnection
        {
            OrganizationId = Guid.CreateVersion7(),
            WorkspaceId = Guid.CreateVersion7(),
            Name = "Template validation",
            Host = "template.invalid",
            Environment = defaults.Environment,
            Notes = defaults.Notes,
            Tags = defaults.Tags.ToList(),
            Metadata = defaults.Metadata.ToDomain(),
            Settings = defaults.Settings.ToDomain()
        };
        Validate(validation.Validate());
    }

    private static ConnectionTemplateDto ToConnectionTemplateDto(ManagedConnectionTemplate entity) => new(
        entity.Id, entity.OrganizationId, entity.WorkspaceId, entity.Name, entity.Description,
        JsonSerializer.Deserialize<TemplateProfileDefaults>(entity.TemplateJson, JsonOptions) ?? throw new InvalidOperationException("Stored connection template is invalid."),
        entity.Revision, entity.UpdatedAtUtc);

    private async Task<UserWorkspaceStateDto> ToSanitizedUserStateAsync(UserWorkspaceState? state, PlatformActor actor, Guid organizationId, Guid workspaceId, CancellationToken cancellationToken)
    {
        var layout = state is null ? new LayoutPreferencesDto(320, "name", "system", true) : JsonSerializer.Deserialize<LayoutPreferencesDto>(state.LayoutJson, JsonOptions) ?? new LayoutPreferencesDto(320, "name", "system", true);
        var pins = state is null ? [] : JsonSerializer.Deserialize<Guid[]>(state.PinnedFolderIdsJson, JsonOptions) ?? [];
        var saved = state is null ? [] : JsonSerializer.Deserialize<SavedWorkspaceDto[]>(state.SavedWorkspacesJson, JsonOptions) ?? [];
        var groups = state is null ? [] : JsonSerializer.Deserialize<TabGroupDto[]>(state.TabGroupsJson, JsonOptions) ?? [];
        var validFolders = await dbContext.Folders.AsNoTracking().Where(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && pins.Contains(entity.Id)).Select(entity => entity.Id).ToListAsync(cancellationToken);
        var permittedConnections = await PermittedConnectionsAsync(actor, organizationId, workspaceId, cancellationToken);
        var permittedIds = permittedConnections.Select(entity => entity.Id).ToHashSet();
        var sanitizedSaved = saved.Select(item => item with { ConnectionIds = item.ConnectionIds.Where(permittedIds.Contains).Distinct().ToArray() }).ToArray();
        var sanitizedGroups = groups.Select(item => item with { ConnectionIds = item.ConnectionIds.Where(permittedIds.Contains).Distinct().ToArray() }).ToArray();
        return new UserWorkspaceStateDto(organizationId, workspaceId, actor.PrincipalId, validFolders, layout, sanitizedSaved, sanitizedGroups,
            state?.Revision ?? 0, state?.UpdatedAtUtc ?? DateTimeOffset.MinValue);
    }

    private static void ValidateWorkspaceState(SaveUserWorkspaceStateRequest request)
    {
        if (request.PinnedFolderIds.Count > 100 || request.PinnedFolderIds.Distinct().Count() != request.PinnedFolderIds.Count)
            throw PlatformProblemException.Validation(["Pin at most 100 unique folders."]);
        if (request.Layout.NavigationWidth is < 200 or > 800 || request.Layout.InventorySort.Length > 50 || request.Layout.Theme is not ("system" or "light" or "dark"))
            throw PlatformProblemException.Validation(["Layout preferences are invalid."]);
        if (request.SavedWorkspaces.Count > 50 || request.TabGroups.Count > 50) throw PlatformProblemException.Validation(["Save at most 50 workspaces and 50 tab groups."]);
        if (request.SavedWorkspaces.Any(item => string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 200 || item.ConnectionIds.Count > 25) ||
            request.TabGroups.Any(item => string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 200 || item.Color.Length > 32 || item.ConnectionIds.Count > 25))
            throw PlatformProblemException.Validation(["Saved workspace or tab-group content is invalid."]);
    }
}
