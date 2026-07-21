using Microsoft.EntityFrameworkCore;
using NextRdp.Platform.Application;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Infrastructure;

public sealed partial class PlatformManagementService
{
    public async Task<IReadOnlyList<FolderDto>> ListFoldersAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ReadInventory, organizationId, workspaceId, "workspace", workspaceId, cancellationToken: cancellationToken);
        var folders = await dbContext.Folders.AsNoTracking()
            .Where(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId)
            .OrderBy(entity => entity.SortOrder)
            .ThenBy(entity => entity.Name)
            .ThenBy(entity => entity.Id)
            .ToListAsync(cancellationToken);
        return folders.Select(entity => entity.ToDto()).ToArray();
    }

    public async Task<FolderDto> SaveFolderAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, Guid? folderId, UpsertFolderRequest request, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ManageSharedInventory, organizationId, workspaceId, "workspace", workspaceId, cancellationToken: cancellationToken);
        await EnsureWorkspaceExistsAsync(organizationId, workspaceId, cancellationToken);
        if (request.ParentId is not null && !await dbContext.Folders.AnyAsync(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && entity.Id == request.ParentId, cancellationToken))
            throw PlatformProblemException.NotFound("parent folder");

        var now = timeProvider.GetUtcNow();
        ManagedFolder entity;
        var created = folderId is null;
        if (created)
        {
            entity = new ManagedFolder
            {
                OrganizationId = organizationId,
                WorkspaceId = workspaceId,
                CreatedAtUtc = now
            };
            dbContext.Folders.Add(entity);
        }
        else
        {
            entity = await dbContext.Folders.SingleOrDefaultAsync(item => item.OrganizationId == organizationId && item.WorkspaceId == workspaceId && item.Id == folderId, cancellationToken)
                ?? throw PlatformProblemException.NotFound("folder");
            RequireRevision(request.ExpectedRevision, entity.Revision);
            entity.Revision++;
        }

        entity.ParentId = request.ParentId;
        entity.Name = request.Name.Trim();
        entity.SortOrder = request.SortOrder;
        entity.UpdatedAtUtc = now;
        Validate(entity.Validate());
        await EnsureFolderHierarchyAsync(entity, cancellationToken);

        var dto = entity.ToDto();
        RecordSync(organizationId, workspaceId, null, "folder", entity.Id, SyncChangeKind.Upsert, entity.Revision, dto);
        RecordAudit(actor, organizationId, workspaceId, created ? "folder.created" : "folder.updated", "folder", entity.Id, AuditOutcome.Succeeded, new { fields = FolderMutationFields, entity.Revision });
        RecordOutbox(created ? "folder.created" : "folder.updated", new { entity.OrganizationId, entity.WorkspaceId, entity.Id, entity.Revision });
        await SaveChangesAsync(cancellationToken);
        return dto;
    }

    public async Task<PagedResult<ManagedConnectionDto>> ListConnectionsAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, string? search, string? cursor, int pageSize, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ReadInventory, organizationId, workspaceId, "workspace", workspaceId, cancellationToken: cancellationToken);
        if (pageSize is < 1 or > 200) throw PlatformProblemException.Validation(["Page size must be between 1 and 200."]);
        var offset = cursorCodec.Decode(cursor);
        if (offset > int.MaxValue) throw PlatformProblemException.Validation(["Page cursor is outside the supported range."]);

        var query = dbContext.Connections.AsNoTracking()
            .Where(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId &&
                (entity.Visibility == ResourceVisibility.Shared || entity.OwnerUserId == actor.PrincipalId));
        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            if (term.Length > 200) throw PlatformProblemException.Validation(["Search text cannot exceed 200 characters."]);
            var pattern = $"%{EscapeLikePattern(term)}%";
            query = query.Where(entity =>
                EF.Functions.ILike(entity.Name, pattern, "\\") ||
                EF.Functions.ILike(entity.Host, pattern, "\\") ||
                entity.Tags.Any(tag => EF.Functions.ILike(tag, pattern, "\\")) ||
                EF.Functions.ILike(entity.Metadata.BusinessUnit, pattern, "\\") ||
                EF.Functions.ILike(entity.Metadata.ApplicationOwner, pattern, "\\") ||
                EF.Functions.ILike(entity.Metadata.ServiceOwner, pattern, "\\") ||
                EF.Functions.ILike(entity.Metadata.Criticality, pattern, "\\") ||
                EF.Functions.ILike(entity.Metadata.Geography, pattern, "\\") ||
                EF.Functions.ILike(entity.Metadata.DataClassification, pattern, "\\") ||
                EF.Functions.ILike(entity.Metadata.MaintenanceWindow, pattern, "\\") ||
                EF.Functions.ILike(entity.Metadata.SupportGroup, pattern, "\\") ||
                EF.Functions.ILike(entity.Metadata.EscalationContact, pattern, "\\") ||
                EF.Functions.ILike(entity.Metadata.AssetId, pattern, "\\") ||
                EF.Functions.ILike(entity.Metadata.CmdbId, pattern, "\\") ||
                EF.Functions.ILike(entity.Metadata.OperatingSystem, pattern, "\\") ||
                EF.Functions.ILike(entity.Metadata.MonitoringUrl, pattern, "\\") ||
                EF.Functions.ILike(entity.Metadata.DocumentationUrl, pattern, "\\") ||
                EF.Functions.ILike(entity.Metadata.TicketingUrl, pattern, "\\"));
        }

        var items = await query.OrderByDescending(entity => entity.UpdatedAtUtc).ThenBy(entity => entity.Id)
            .Skip((int)offset)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);
        var hasMore = items.Count > pageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);
        var nextCursor = hasMore ? cursorCodec.Encode(offset + items.Count) : null;
        return new PagedResult<ManagedConnectionDto>(items.Select(entity => entity.ToDto()).ToArray(), nextCursor, pageSize);
    }

    public async Task<ManagedConnectionDto> GetConnectionAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, Guid connectionId, CancellationToken cancellationToken = default)
    {
        await DemandAsync(actor, PlatformAction.ReadInventory, organizationId, workspaceId, "connection", connectionId, cancellationToken: cancellationToken);
        var entity = await dbContext.Connections.AsNoTracking().SingleOrDefaultAsync(item =>
            item.OrganizationId == organizationId && item.WorkspaceId == workspaceId && item.Id == connectionId &&
            (item.Visibility == ResourceVisibility.Shared || item.OwnerUserId == actor.PrincipalId), cancellationToken);
        return entity?.ToDto() ?? throw PlatformProblemException.NotFound("connection");
    }

    public async Task<ManagedConnectionDto> SaveConnectionAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, Guid? connectionId, UpsertManagedConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var action = request.Visibility == ResourceVisibility.Personal ? PlatformAction.ManagePersonalInventory : PlatformAction.ManageSharedInventory;
        await DemandAsync(actor, action, organizationId, workspaceId, connectionId is null ? "workspace" : "connection", connectionId ?? workspaceId, cancellationToken: cancellationToken);
        await EnsureWorkspaceExistsAsync(organizationId, workspaceId, cancellationToken);
        if (request.FolderId is not null && !await dbContext.Folders.AnyAsync(entity => entity.OrganizationId == organizationId && entity.WorkspaceId == workspaceId && entity.Id == request.FolderId, cancellationToken))
            throw PlatformProblemException.NotFound("folder");

        var now = timeProvider.GetUtcNow();
        ManagedConnection entity;
        var created = connectionId is null;
        if (created)
        {
            entity = new ManagedConnection
            {
                OrganizationId = organizationId,
                WorkspaceId = workspaceId,
                CreatedAtUtc = now
            };
            dbContext.Connections.Add(entity);
        }
        else
        {
            entity = await dbContext.Connections.SingleOrDefaultAsync(item => item.OrganizationId == organizationId && item.WorkspaceId == workspaceId && item.Id == connectionId, cancellationToken)
                ?? throw PlatformProblemException.NotFound("connection");
            if (entity.Visibility == ResourceVisibility.Personal && entity.OwnerUserId != actor.PrincipalId)
                throw PlatformProblemException.NotFound("connection");
            RequireRevision(request.ExpectedRevision, entity.Revision);
            entity.Revision++;
        }

        entity.FolderId = request.FolderId;
        entity.Visibility = request.Visibility;
        entity.OwnerUserId = request.Visibility == ResourceVisibility.Personal ? actor.PrincipalId : null;
        entity.Name = request.Name.Trim();
        entity.Host = request.Host.Trim();
        entity.Port = request.Port;
        entity.UsernameHint = request.UsernameHint.Trim();
        entity.Environment = request.Environment;
        entity.Notes = request.Notes.Trim();
        entity.Tags = request.Tags.Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        entity.Metadata = request.Metadata.ToDomain();
        entity.Settings = request.Settings.ToDomain();
        entity.UpdatedAtUtc = now;
        Validate(entity.Validate());

        var dto = entity.ToDto();
        RecordSync(organizationId, workspaceId, entity.OwnerUserId, "connection", entity.Id, SyncChangeKind.Upsert, entity.Revision, dto);
        RecordAudit(actor, organizationId, workspaceId, created ? "connection.created" : "connection.updated", "connection", entity.Id, AuditOutcome.Succeeded,
            new { fields = ConnectionMutationFields, entity.Revision });
        RecordOutbox(created ? "connection.created" : "connection.updated", new { entity.OrganizationId, entity.WorkspaceId, entity.Id, entity.Revision, entity.Visibility });
        await SaveChangesAsync(cancellationToken);
        return dto;
    }

    public async Task DeleteConnectionAsync(PlatformActor actor, Guid organizationId, Guid workspaceId, Guid connectionId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.Connections.SingleOrDefaultAsync(item => item.OrganizationId == organizationId && item.WorkspaceId == workspaceId && item.Id == connectionId, cancellationToken)
            ?? throw PlatformProblemException.NotFound("connection");
        if (entity.Visibility == ResourceVisibility.Personal && entity.OwnerUserId != actor.PrincipalId) throw PlatformProblemException.NotFound("connection");
        var action = entity.Visibility == ResourceVisibility.Personal ? PlatformAction.ManagePersonalInventory : PlatformAction.ManageSharedInventory;
        await DemandAsync(actor, action, organizationId, workspaceId, "connection", connectionId, cancellationToken: cancellationToken);
        RequireRevision(expectedRevision, entity.Revision);

        dbContext.Connections.Remove(entity);
        var tombstoneRevision = entity.Revision + 1;
        RecordSync(organizationId, workspaceId, entity.OwnerUserId, "connection", entity.Id, SyncChangeKind.Delete, tombstoneRevision, null);
        RecordAudit(actor, organizationId, workspaceId, "connection.deleted", "connection", entity.Id, AuditOutcome.Succeeded, new { revision = tombstoneRevision });
        RecordOutbox("connection.deleted", new { entity.OrganizationId, entity.WorkspaceId, entity.Id, revision = tombstoneRevision, entity.Visibility });
        await SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureFolderHierarchyAsync(ManagedFolder folder, CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid> { folder.Id };
        var parentId = folder.ParentId;
        var depth = 1;
        while (parentId is not null)
        {
            if (!visited.Add(parentId.Value)) throw PlatformProblemException.Validation(["Folder hierarchy contains a cycle."]);
            var parent = await dbContext.Folders.AsNoTracking().SingleOrDefaultAsync(entity =>
                entity.OrganizationId == folder.OrganizationId && entity.WorkspaceId == folder.WorkspaceId && entity.Id == parentId, cancellationToken)
                ?? throw PlatformProblemException.NotFound("parent folder");
            parentId = parent.ParentId;
            depth++;
            if (depth > 32) throw PlatformProblemException.Validation(["Folder hierarchy cannot exceed 32 levels."]);
        }
    }

    private static void RequireRevision(long? expected, long actual)
    {
        if (expected is null) throw new PlatformProblemException(428, "precondition_required", "Revision required", "Supply the current resource revision before updating or deleting it.");
        if (expected != actual) throw PlatformProblemException.Conflict($"Expected revision {expected.Value}, but the current revision is {actual}.");
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
