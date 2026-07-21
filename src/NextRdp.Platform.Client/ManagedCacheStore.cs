using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Client;

internal sealed class ManagedCacheDbContext(DbContextOptions<ManagedCacheDbContext> options) : DbContext(options)
{
    public DbSet<ManagedCacheScopeEntity> Scopes => Set<ManagedCacheScopeEntity>();
    public DbSet<ManagedCacheItemEntity> Items => Set<ManagedCacheItemEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var scope = modelBuilder.Entity<ManagedCacheScopeEntity>();
        scope.HasKey(entity => new { entity.OrganizationId, entity.WorkspaceId });
        scope.Property(entity => entity.OrganizationName).HasMaxLength(200);
        scope.Property(entity => entity.WorkspaceName).HasMaxLength(200);
        scope.Property(entity => entity.Cursor).HasMaxLength(4096);
        scope.Property(entity => entity.MinimumClientVersion).HasMaxLength(64);
        scope.HasIndex(entity => entity.LastSelectedAtUtc);

        var item = modelBuilder.Entity<ManagedCacheItemEntity>();
        item.HasKey(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.ResourceType, entity.ResourceId });
        item.Property(entity => entity.ResourceType).HasMaxLength(100);
        item.Property(entity => entity.PayloadJson).HasColumnType("TEXT");
        item.HasIndex(entity => new { entity.OrganizationId, entity.WorkspaceId, entity.ResourceType });
    }
}

internal sealed class ManagedCacheScopeEntity
{
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string OrganizationName { get; set; } = string.Empty;
    public string WorkspaceName { get; set; } = string.Empty;
    public string Cursor { get; set; } = string.Empty;
    public DateTimeOffset? LastSyncAtUtc { get; set; }
    public DateTimeOffset LastSelectedAtUtc { get; set; }
    public string MinimumClientVersion { get; set; } = "0.1.0";
    public bool ClientUpgradeRequired { get; set; }
    public long EffectivePolicyRevision { get; set; }
    public bool AccessRevoked { get; set; }
}

internal sealed class ManagedCacheItemEntity
{
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string ResourceType { get; set; } = string.Empty;
    public Guid ResourceId { get; set; }
    public long Revision { get; set; }
    public string PayloadJson { get; set; } = "{}";
}

public sealed class ManagedCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly DbContextOptions<ManagedCacheDbContext> options;

    public ManagedCacheStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        options = new DbContextOptionsBuilder<ManagedCacheDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ManagedCacheDbContext(options);
        await db.Database.EnsureCreatedAsync(cancellationToken);
    }

    public async Task SelectAsync(ManagedWorkspaceSelection selection, CancellationToken cancellationToken = default)
    {
        await using var db = new ManagedCacheDbContext(options);
        var scope = await db.Scopes.SingleOrDefaultAsync(entity => entity.OrganizationId == selection.OrganizationId && entity.WorkspaceId == selection.WorkspaceId, cancellationToken);
        if (scope is null)
        {
            scope = new ManagedCacheScopeEntity { OrganizationId = selection.OrganizationId, WorkspaceId = selection.WorkspaceId };
            db.Scopes.Add(scope);
        }
        scope.OrganizationName = selection.OrganizationName;
        scope.WorkspaceName = selection.WorkspaceName;
        scope.LastSelectedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ManagedWorkspaceSelection?> LastSelectionAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ManagedCacheDbContext(options);
        var scopes = await db.Scopes.AsNoTracking().ToListAsync(cancellationToken);
        var scope = scopes.OrderByDescending(entity => entity.LastSelectedAtUtc).FirstOrDefault();
        return scope is null ? null : new ManagedWorkspaceSelection(scope.OrganizationId, scope.WorkspaceId, scope.OrganizationName, scope.WorkspaceName);
    }

    public async Task<string?> CursorAsync(Guid organizationId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var db = new ManagedCacheDbContext(options);
        return await db.Scopes.AsNoTracking().Where(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId).Select(entity => entity.Cursor).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, long>> KnownRevisionsAsync(Guid organizationId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await using var db = new ManagedCacheDbContext(options);
        return await db.Items.AsNoTracking().Where(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && entity.ResourceType != "effectivePolicy" && !entity.ResourceType.EndsWith("Snapshot"))
            .ToDictionaryAsync(entity => entity.ResourceId, entity => entity.Revision, cancellationToken);
    }

    public async Task ApplyAsync(ManagedWorkspaceSelection selection, SyncResponse response, CancellationToken cancellationToken = default)
    {
        await using var db = new ManagedCacheDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var scope = await db.Scopes.SingleAsync(entity => entity.OrganizationId == selection.OrganizationId && entity.WorkspaceId == selection.WorkspaceId, cancellationToken);
        var accessRevoked = response.Items.Any(change => change.Kind == SyncChangeKind.AccessRevoked);
        if (accessRevoked)
        {
            scope.AccessRevoked = true;
            await db.Items.Where(entity => entity.OrganizationId == selection.OrganizationId && entity.WorkspaceId == selection.WorkspaceId).ExecuteDeleteAsync(cancellationToken);
        }
        var applicableChanges = response.Items
            .Where(change => !accessRevoked && change.Kind != SyncChangeKind.AccessRevoked)
            .Select((change, index) => new { Change = change, Index = index })
            .GroupBy(item => new { item.Change.ResourceType, item.Change.ResourceId })
            .Select(group => group
                .OrderByDescending(item => item.Change.Revision)
                .ThenByDescending(item => item.Index)
                .First())
            .OrderBy(item => item.Index)
            .Select(item => item.Change);
        foreach (var change in applicableChanges)
        {
            var item = await db.Items.SingleOrDefaultAsync(entity => entity.OrganizationId == selection.OrganizationId && entity.WorkspaceId == selection.WorkspaceId && entity.ResourceType == change.ResourceType && entity.ResourceId == change.ResourceId, cancellationToken);
            if (change.Kind == SyncChangeKind.Delete)
            {
                if (item is not null) db.Items.Remove(item);
                if (change.ResourceType == "connection")
                {
                    var policy = await db.Items.SingleOrDefaultAsync(entity => entity.OrganizationId == selection.OrganizationId && entity.WorkspaceId == selection.WorkspaceId && entity.ResourceType == "effectivePolicy" && entity.ResourceId == change.ResourceId, cancellationToken);
                    if (policy is not null) db.Items.Remove(policy);
                }
                continue;
            }
            if (change.Payload is null) throw new InvalidDataException("A synchronization upsert did not include a payload.");
            if (item is null)
            {
                item = new ManagedCacheItemEntity { OrganizationId = selection.OrganizationId, WorkspaceId = selection.WorkspaceId, ResourceType = change.ResourceType, ResourceId = change.ResourceId };
                db.Items.Add(item);
            }
            if (change.Revision < item.Revision) continue;
            item.Revision = change.Revision;
            item.PayloadJson = change.Payload.Value.GetRawText();
        }
        scope.Cursor = response.NextCursor;
        scope.LastSyncAtUtc = response.ServerTimeUtc;
        scope.MinimumClientVersion = response.MinimumClientVersion;
        scope.ClientUpgradeRequired = response.ClientUpgradeRequired;
        scope.EffectivePolicyRevision = response.EffectivePolicyRevision;
        if (!accessRevoked) scope.AccessRevoked = false;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RevokeAsync(ManagedWorkspaceSelection selection, CancellationToken cancellationToken = default)
    {
        await using var db = new ManagedCacheDbContext(options);
        var scope = await db.Scopes.SingleOrDefaultAsync(entity => entity.OrganizationId == selection.OrganizationId && entity.WorkspaceId == selection.WorkspaceId, cancellationToken);
        if (scope is not null) scope.AccessRevoked = true;
        await db.Items.Where(entity => entity.OrganizationId == selection.OrganizationId && entity.WorkspaceId == selection.WorkspaceId).ExecuteDeleteAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveEffectivePolicyAsync(ManagedWorkspaceSelection selection, EffectivePolicy policy, CancellationToken cancellationToken = default)
    {
        if (policy.ConnectionId is not Guid connectionId) return;
        await using var db = new ManagedCacheDbContext(options);
        var item = await db.Items.SingleOrDefaultAsync(entity => entity.OrganizationId == selection.OrganizationId && entity.WorkspaceId == selection.WorkspaceId && entity.ResourceType == "effectivePolicy" && entity.ResourceId == connectionId, cancellationToken);
        if (item is null)
        {
            item = new ManagedCacheItemEntity { OrganizationId = selection.OrganizationId, WorkspaceId = selection.WorkspaceId, ResourceType = "effectivePolicy", ResourceId = connectionId };
            db.Items.Add(item);
        }
        item.Revision = policy.Revision;
        item.PayloadJson = JsonSerializer.Serialize(policy, JsonOptions);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, EffectivePolicy>> LoadEffectivePoliciesAsync(ManagedWorkspaceSelection selection, CancellationToken cancellationToken = default)
    {
        await using var db = new ManagedCacheDbContext(options);
        var items = await db.Items.AsNoTracking().Where(entity => entity.OrganizationId == selection.OrganizationId && entity.WorkspaceId == selection.WorkspaceId && entity.ResourceType == "effectivePolicy").ToListAsync(cancellationToken);
        return items.Select(item => JsonSerializer.Deserialize<EffectivePolicy>(item.PayloadJson, JsonOptions) ?? throw new InvalidDataException("Cached effective policy payload is invalid."))
            .Where(policy => policy.ConnectionId is not null)
            .ToDictionary(policy => policy.ConnectionId!.Value);
    }

    public async Task SaveProductivityAsync(ManagedWorkspaceSelection selection, ManagedProductivitySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var db = new ManagedCacheDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Items.Where(entity => entity.OrganizationId == selection.OrganizationId && entity.WorkspaceId == selection.WorkspaceId &&
            (entity.ResourceType == "dynamicGroupSnapshot" || entity.ResourceType == "templateSnapshot" || entity.ResourceType == "workspaceStateSnapshot")).ExecuteDeleteAsync(cancellationToken);
        foreach (var group in snapshot.DynamicGroups) db.Items.Add(Snapshot(selection, "dynamicGroupSnapshot", group.Id, group.Revision, group));
        foreach (var template in snapshot.Templates) db.Items.Add(Snapshot(selection, "templateSnapshot", template.Id, template.Revision, template));
        db.Items.Add(Snapshot(selection, "workspaceStateSnapshot", snapshot.WorkspaceState.UserId, snapshot.WorkspaceState.Revision, snapshot.WorkspaceState));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ManagedProductivitySnapshot?> LoadProductivityAsync(ManagedWorkspaceSelection selection, CancellationToken cancellationToken = default)
    {
        await using var db = new ManagedCacheDbContext(options);
        var items = await db.Items.AsNoTracking().Where(entity => entity.OrganizationId == selection.OrganizationId && entity.WorkspaceId == selection.WorkspaceId &&
            (entity.ResourceType == "dynamicGroupSnapshot" || entity.ResourceType == "templateSnapshot" || entity.ResourceType == "workspaceStateSnapshot")).ToListAsync(cancellationToken);
        var state = Deserialize<UserWorkspaceStateDto>(items, "workspaceStateSnapshot").SingleOrDefault();
        return state is null ? null : new ManagedProductivitySnapshot(Deserialize<DynamicGroupDto>(items, "dynamicGroupSnapshot"), Deserialize<ConnectionTemplateDto>(items, "templateSnapshot"), state);
    }

    private static ManagedCacheItemEntity Snapshot<T>(ManagedWorkspaceSelection selection, string type, Guid id, long revision, T value) => new()
    {
        OrganizationId = selection.OrganizationId,
        WorkspaceId = selection.WorkspaceId,
        ResourceType = type,
        ResourceId = id,
        Revision = revision,
        PayloadJson = JsonSerializer.Serialize(value, JsonOptions)
    };

    public async Task<(IReadOnlyList<ManagedConnectionDto> Connections, IReadOnlyList<FolderDto> Folders, ManagedCacheStatus Status)> LoadAsync(ManagedWorkspaceSelection selection, bool online, string message, CancellationToken cancellationToken = default)
    {
        await using var db = new ManagedCacheDbContext(options);
        var scope = await db.Scopes.AsNoTracking().SingleOrDefaultAsync(entity => entity.OrganizationId == selection.OrganizationId && entity.WorkspaceId == selection.WorkspaceId, cancellationToken)
            ?? new ManagedCacheScopeEntity { OrganizationId = selection.OrganizationId, WorkspaceId = selection.WorkspaceId, OrganizationName = selection.OrganizationName, WorkspaceName = selection.WorkspaceName };
        var items = await db.Items.AsNoTracking().Where(entity => entity.OrganizationId == selection.OrganizationId && entity.WorkspaceId == selection.WorkspaceId).ToListAsync(cancellationToken);
        var connections = Deserialize<ManagedConnectionDto>(items, "connection");
        var folders = Deserialize<FolderDto>(items, "folder");
        var status = new ManagedCacheStatus(selection.OrganizationId, selection.WorkspaceId, online, !online || scope.ClientUpgradeRequired || scope.AccessRevoked,
            scope.AccessRevoked, scope.LastSyncAtUtc, scope.MinimumClientVersion, scope.ClientUpgradeRequired, scope.EffectivePolicyRevision, message);
        return (connections, folders, status);
    }

    private static T[] Deserialize<T>(IEnumerable<ManagedCacheItemEntity> items, string resourceType) => items
        .Where(item => item.ResourceType.Equals(resourceType, StringComparison.Ordinal))
        .Select(item => JsonSerializer.Deserialize<T>(item.PayloadJson, JsonOptions) ?? throw new InvalidDataException($"Cached {resourceType} payload is invalid."))
        .ToArray();
}
