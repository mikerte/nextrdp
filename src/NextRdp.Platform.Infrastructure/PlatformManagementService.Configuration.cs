using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Infrastructure;

public sealed partial class PlatformManagementService
{
    public async Task<ConfigurationDraftDto> SaveConfigurationDraftAsync(
        PlatformActor actor,
        Guid organizationId,
        Guid? workspaceId,
        Guid? draftId,
        SaveConfigurationDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ManageConfiguration, organizationId, workspaceId, ScopeResourceType(request.ScopeType), request.ScopeId, cancellationToken: cancellationToken);
        await ValidateConfigurationScopeAsync(organizationId, workspaceId, request.ScopeType, request.ScopeId, cancellationToken);
        var validation = PolicySchema.Validate(request.Values, request.LockedFields);
        Validate(validation);

        var latestVersion = await LatestVersionAsync(organizationId, workspaceId, request.ScopeType, request.ScopeId, cancellationToken);
        if (request.BaseVersion != latestVersion)
            throw PlatformProblemException.Conflict($"Draft base version {request.BaseVersion} does not match the latest published version {latestVersion}.");

        var now = timeProvider.GetUtcNow();
        ConfigurationDraft draft;
        var created = draftId is null;
        if (created)
        {
            draft = new ConfigurationDraft
            {
                OrganizationId = organizationId,
                WorkspaceId = workspaceId,
                ScopeType = request.ScopeType,
                ScopeId = request.ScopeId,
                CreatedByUserId = actor.PrincipalId,
                CreatedAtUtc = now
            };
            dbContext.ConfigurationDrafts.Add(draft);
        }
        else
        {
            draft = await dbContext.ConfigurationDrafts.SingleOrDefaultAsync(entity =>
                entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && entity.Id == draftId && entity.Lifecycle == ConfigurationLifecycle.Draft, cancellationToken)
                ?? throw PlatformProblemException.NotFound("configuration draft");
            RequireRevision(request.ExpectedRevision, draft.Revision);
            if (draft.ScopeType != request.ScopeType || draft.ScopeId != request.ScopeId)
                throw PlatformProblemException.Conflict("A configuration draft cannot change its scope.");
            draft.Revision++;
        }

        draft.BaseVersion = request.BaseVersion;
        draft.ValuesJson = request.Values.GetRawText();
        draft.LockedFieldsJson = JsonSerializer.Serialize(request.LockedFields.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), JsonOptions);
        draft.Summary = request.Summary.Trim();
        draft.UpdatedAtUtc = now;
        Validate(draft.Validate());

        RecordAudit(actor, organizationId, workspaceId, created ? "configuration.draft_created" : "configuration.draft_updated", "configurationDraft", draft.Id, AuditOutcome.Succeeded,
            new { draft.ScopeType, draft.ScopeId, draft.BaseVersion, draft.Revision, fields = request.Values.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray(), lockedFields = request.LockedFields.Order(StringComparer.Ordinal).ToArray() });
        await SaveChangesAsync(cancellationToken);
        return ToDto(draft);
    }

    public async Task<PublishedConfigurationDto> PublishConfigurationAsync(PlatformActor actor, Guid organizationId, Guid? workspaceId, Guid draftId, CancellationToken cancellationToken = default)
    {
        if (!actor.MfaSatisfied) throw PlatformProblemException.Forbidden("The identity provider must confirm MFA before publishing central policy.");
        var draft = await dbContext.ConfigurationDrafts.SingleOrDefaultAsync(entity =>
            entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && entity.Id == draftId && entity.Lifecycle == ConfigurationLifecycle.Draft, cancellationToken)
            ?? throw PlatformProblemException.NotFound("configuration draft");
        await DemandAsync(actor, PlatformAction.PublishConfiguration, organizationId, workspaceId, ScopeResourceType(draft.ScopeType), draft.ScopeId, cancellationToken: cancellationToken);

        var latestVersion = await LatestVersionAsync(organizationId, workspaceId, draft.ScopeType, draft.ScopeId, cancellationToken);
        if (draft.BaseVersion != latestVersion)
            throw PlatformProblemException.Conflict($"The draft is based on version {draft.BaseVersion}, but version {latestVersion} is now published.");

        using var values = JsonDocument.Parse(draft.ValuesJson);
        var locks = JsonSerializer.Deserialize<string[]>(draft.LockedFieldsJson, JsonOptions) ?? [];
        Validate(PolicySchema.Validate(values.RootElement, locks));

        var published = new PublishedConfiguration
        {
            OrganizationId = organizationId,
            WorkspaceId = workspaceId,
            ScopeType = draft.ScopeType,
            ScopeId = draft.ScopeId,
            Version = latestVersion + 1,
            SourceDraftId = draft.Id,
            ValuesJson = draft.ValuesJson,
            LockedFieldsJson = draft.LockedFieldsJson,
            PublishedByUserId = actor.PrincipalId,
            Summary = draft.Summary,
            PublishedAtUtc = timeProvider.GetUtcNow()
        };
        draft.Lifecycle = ConfigurationLifecycle.Published;
        draft.UpdatedAtUtc = published.PublishedAtUtc;
        draft.Revision++;
        dbContext.PublishedConfigurations.Add(published);
        await RecordPolicySyncAsync(published, cancellationToken);
        RecordAudit(actor, organizationId, workspaceId, "configuration.published", "configuration", published.Id, AuditOutcome.Succeeded,
            new { published.ScopeType, published.ScopeId, published.Version, published.SourceDraftId });
        RecordOutbox("configuration.published", new { published.OrganizationId, published.WorkspaceId, published.Id, published.ScopeType, published.ScopeId, published.Version });
        await SaveChangesAsync(cancellationToken);
        return ToDto(published);
    }

    public async Task<PublishedConfigurationDto> RollbackConfigurationAsync(
        PlatformActor actor,
        Guid organizationId,
        Guid? workspaceId,
        Guid publishedConfigurationId,
        string summary,
        CancellationToken cancellationToken = default)
    {
        if (!actor.MfaSatisfied) throw PlatformProblemException.Forbidden("The identity provider must confirm MFA before rolling back central policy.");
        var source = await dbContext.PublishedConfigurations.AsNoTracking().SingleOrDefaultAsync(entity =>
            entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && entity.Id == publishedConfigurationId, cancellationToken)
            ?? throw PlatformProblemException.NotFound("published configuration");
        await DemandAsync(actor, PlatformAction.PublishConfiguration, organizationId, workspaceId, ScopeResourceType(source.ScopeType), source.ScopeId, cancellationToken: cancellationToken);
        if (summary.Length > 500 || summary.Any(char.IsControl)) throw PlatformProblemException.Validation(["Rollback summary cannot exceed 500 characters or contain control characters."]);
        var latestVersion = await LatestVersionAsync(organizationId, workspaceId, source.ScopeType, source.ScopeId, cancellationToken);

        var published = new PublishedConfiguration
        {
            OrganizationId = source.OrganizationId,
            WorkspaceId = source.WorkspaceId,
            ScopeType = source.ScopeType,
            ScopeId = source.ScopeId,
            Version = latestVersion + 1,
            SourceDraftId = source.SourceDraftId,
            RolledBackFromId = source.Id,
            ValuesJson = source.ValuesJson,
            LockedFieldsJson = source.LockedFieldsJson,
            PublishedByUserId = actor.PrincipalId,
            Summary = summary.Trim(),
            PublishedAtUtc = timeProvider.GetUtcNow()
        };
        dbContext.PublishedConfigurations.Add(published);
        await RecordPolicySyncAsync(published, cancellationToken);
        RecordAudit(actor, organizationId, workspaceId, "configuration.rolled_back", "configuration", published.Id, AuditOutcome.Succeeded,
            new { sourceConfigurationId = source.Id, published.Version, published.ScopeType, published.ScopeId });
        RecordOutbox("configuration.published", new { published.OrganizationId, published.WorkspaceId, published.Id, published.ScopeType, published.ScopeId, published.Version, published.RolledBackFromId });
        await SaveChangesAsync(cancellationToken);
        return ToDto(published);
    }

    public async Task<IReadOnlyList<PublishedConfigurationDto>> ListConfigurationHistoryAsync(
        PlatformActor actor,
        Guid organizationId,
        Guid? workspaceId,
        ConfigurationScopeType scopeType,
        Guid scopeId,
        CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ReadConfiguration, organizationId, workspaceId, ScopeResourceType(scopeType), scopeId, cancellationToken: cancellationToken);
        var items = await dbContext.PublishedConfigurations.AsNoTracking()
            .Where(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && entity.ScopeType == scopeType && entity.ScopeId == scopeId)
            .OrderByDescending(entity => entity.Version)
            .Take(200)
            .ToListAsync(cancellationToken);
        return items.Select(ToDto).ToArray();
    }

    public async Task<EffectivePolicy> GetEffectivePolicyAsync(
        PlatformActor actor,
        Guid organizationId,
        Guid workspaceId,
        Guid? folderId,
        Guid? connectionId,
        CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ReadConfiguration, organizationId, workspaceId, connectionId is null ? "workspace" : "connection", connectionId ?? workspaceId, cancellationToken: cancellationToken);
        await EnsureWorkspaceExistsAsync(organizationId, workspaceId, cancellationToken);
        var scopeKeys = new List<(ConfigurationScopeType Type, Guid Id)>
        {
            (ConfigurationScopeType.Organization, organizationId),
            (ConfigurationScopeType.Workspace, workspaceId)
        };
        if (folderId is not null) scopeKeys.Add((ConfigurationScopeType.Folder, folderId.Value));
        if (connectionId is not null) scopeKeys.Add((ConfigurationScopeType.Connection, connectionId.Value));

        var layers = new List<ConfigurationLayerInput>();
        foreach (var (type, id) in scopeKeys)
        {
            Guid? workspaceScope = type == ConfigurationScopeType.Organization ? null : workspaceId;
            var latest = await dbContext.PublishedConfigurations.AsNoTracking()
                .Where(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceScope && entity.ScopeType == type && entity.ScopeId == id)
                .OrderByDescending(entity => entity.Version)
                .FirstOrDefaultAsync(cancellationToken);
            if (latest is null) continue;
            using var values = JsonDocument.Parse(latest.ValuesJson);
            var locks = JsonSerializer.Deserialize<string[]>(latest.LockedFieldsJson, JsonOptions) ?? [];
            layers.Add(new ConfigurationLayerInput(type, id, latest.Version, values.RootElement.Clone(), locks.ToHashSet(StringComparer.Ordinal)));
        }

        return ConfigurationResolver.Resolve(organizationId, workspaceId, folderId, connectionId, layers);
    }

    private async Task ValidateConfigurationScopeAsync(Guid organizationId, Guid? workspaceId, ConfigurationScopeType scopeType, Guid scopeId, CancellationToken cancellationToken)
    {
        switch (scopeType)
        {
            case ConfigurationScopeType.Organization:
                if (workspaceId is not null || scopeId != organizationId) throw PlatformProblemException.Validation(["Organization configuration must use the organization as its scope and no workspace."]);
                await EnsureOrganizationExistsAsync(organizationId, cancellationToken);
                break;
            case ConfigurationScopeType.Workspace:
                if (workspaceId is null || scopeId != workspaceId) throw PlatformProblemException.Validation(["Workspace configuration scope does not match the route."]);
                await EnsureWorkspaceExistsAsync(organizationId, workspaceId.Value, cancellationToken);
                break;
            case ConfigurationScopeType.Folder:
                if (workspaceId is null || !await dbContext.Folders.AnyAsync(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && entity.Id == scopeId, cancellationToken))
                    throw PlatformProblemException.NotFound("folder");
                break;
            case ConfigurationScopeType.Connection:
                if (workspaceId is null || !await dbContext.Connections.AnyAsync(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && entity.Id == scopeId, cancellationToken))
                    throw PlatformProblemException.NotFound("connection");
                break;
        }
    }

    private Task<int> LatestVersionAsync(Guid organizationId, Guid? workspaceId, ConfigurationScopeType scopeType, Guid scopeId, CancellationToken cancellationToken) =>
        dbContext.PublishedConfigurations
            .Where(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && entity.ScopeType == scopeType && entity.ScopeId == scopeId)
            .Select(entity => (int?)entity.Version)
            .MaxAsync(cancellationToken)
            .ContinueWith(task => task.Result ?? 0, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private async Task RecordPolicySyncAsync(PublishedConfiguration published, CancellationToken cancellationToken)
    {
        if (published.WorkspaceId is not null)
        {
            RecordSync(published.OrganizationId, published.WorkspaceId.Value, null, "configuration", published.Id, SyncChangeKind.Upsert, published.Version, ToDto(published));
            return;
        }

        var workspaceIds = await dbContext.Workspaces.AsNoTracking().Where(entity => entity.OrganizationId == published.OrganizationId).Select(entity => entity.Id).ToListAsync(cancellationToken);
        foreach (var workspaceId in workspaceIds)
            RecordSync(published.OrganizationId, workspaceId, null, "configuration", published.Id, SyncChangeKind.Upsert, published.Version, ToDto(published));
    }

    private static ConfigurationDraftDto ToDto(ConfigurationDraft draft)
    {
        using var values = JsonDocument.Parse(draft.ValuesJson);
        var locks = JsonSerializer.Deserialize<string[]>(draft.LockedFieldsJson, JsonOptions) ?? [];
        return new ConfigurationDraftDto(draft.Id, draft.OrganizationId, draft.WorkspaceId, draft.ScopeType, draft.ScopeId, draft.BaseVersion,
            values.RootElement.Clone(), locks, draft.Summary, draft.Revision, draft.UpdatedAtUtc);
    }

    private static PublishedConfigurationDto ToDto(PublishedConfiguration published)
    {
        using var values = JsonDocument.Parse(published.ValuesJson);
        var locks = JsonSerializer.Deserialize<string[]>(published.LockedFieldsJson, JsonOptions) ?? [];
        return new PublishedConfigurationDto(published.Id, published.OrganizationId, published.WorkspaceId, published.ScopeType, published.ScopeId, published.Version,
            published.RolledBackFromId, values.RootElement.Clone(), locks, published.Summary, published.PublishedAtUtc);
    }

    private static string ScopeResourceType(ConfigurationScopeType scopeType) => scopeType switch
    {
        ConfigurationScopeType.Organization => "organization",
        ConfigurationScopeType.Workspace => "workspace",
        ConfigurationScopeType.Folder => "folder",
        ConfigurationScopeType.Connection => "connection",
        _ => throw new ArgumentOutOfRangeException(nameof(scopeType))
    };
}
