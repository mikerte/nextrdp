using Microsoft.EntityFrameworkCore;
using NextRdp.Core;

namespace NextRdp.Infrastructure;

public sealed class SqliteConnectionRepository(IDbContextFactory<NextRdpDbContext> factory) : IConnectionRepository
{
    public async Task<IReadOnlyList<ConnectionProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Connections.AsNoTracking().OrderByDescending(x => x.IsFavorite).ThenBy(x => x.Name).ToListAsync(cancellationToken);
    }

    public async Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Connections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        var errors = profile.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(profile));
        profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (profile.FolderId is { } folderId && !await db.Folders.AnyAsync(x => x.Id == folderId, cancellationToken))
            throw new InvalidOperationException("The selected folder no longer exists.");
        var exists = await db.Connections.AnyAsync(x => x.Id == profile.Id, cancellationToken);
        db.Entry(profile).State = exists ? EntityState.Modified : EntityState.Added;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Connections.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConnectionFolder>> ListFoldersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Folders.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync(cancellationToken);
    }

    public async Task SaveFolderAsync(ConnectionFolder folder, CancellationToken cancellationToken = default)
    {
        folder.Name = folder.Name.Trim();
        var errors = folder.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(folder));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var folderParents = await db.Folders.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.ParentId, cancellationToken);
        if (folder.ParentId is { } requestedParent)
        {
            if (!folderParents.ContainsKey(requestedParent)) throw new InvalidOperationException("The selected parent folder no longer exists.");
            var visited = new HashSet<Guid>();
            Guid? current = requestedParent;
            var depth = 1;
            while (current is { } currentId)
            {
                depth++;
                if (depth > ConnectionFolder.MaximumDepth)
                    throw new InvalidOperationException($"Folder hierarchy cannot exceed {ConnectionFolder.MaximumDepth} levels.");
                if (currentId == folder.Id) throw new InvalidOperationException("A folder cannot be moved into one of its descendants.");
                if (!visited.Add(currentId)) throw new InvalidOperationException("The existing folder hierarchy contains a cycle and cannot be modified safely.");
                if (!folderParents.TryGetValue(currentId, out current))
                    throw new InvalidOperationException("The existing folder hierarchy references a missing parent and cannot be modified safely.");
            }
        }
        var siblingFolders = await db.Folders.AsNoTracking()
            .Where(x => x.Id != folder.Id && x.ParentId == folder.ParentId)
            .Select(x => x.Name)
            .ToListAsync(cancellationToken);
        if (siblingFolders.Any(name => string.Equals(name, folder.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A folder with this name already exists under the selected parent.");
        var exists = await db.Folders.AnyAsync(x => x.Id == folder.Id, cancellationToken);
        db.Entry(folder).State = exists ? EntityState.Modified : EntityState.Added;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteFolderAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (await db.Folders.AnyAsync(x => x.ParentId == id, cancellationToken))
            throw new InvalidOperationException("Move or delete the nested folders before deleting this folder.");
        if (await db.Connections.AnyAsync(x => x.FolderId == id, cancellationToken))
            throw new InvalidOperationException("Move the connections out of this folder before deleting it.");
        await db.Folders.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken);
    }
}

public sealed class SqliteAuditStore(IDbContextFactory<NextRdpDbContext> factory) : IAuditStore
{
    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        auditEvent.RedactedDetail = SecretRedactor.Redact(auditEvent.RedactedDetail);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.AuditEvents.Add(auditEvent);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEvent>> ListRecentAsync(int limit = 500, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var events = await db.AuditEvents.AsNoTracking().ToListAsync(cancellationToken);
        return events.OrderByDescending(x => x.OccurredAtUtc).Take(limit).ToArray();
    }

    public async Task<int> CloseInterruptedSessionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var events = (await db.AuditEvents.AsNoTracking()
            .Where(x => x.SessionId != null)
            .ToListAsync(cancellationToken))
            .OrderBy(x => x.OccurredAtUtc)
            .ToArray();
        var interrupted = events
            .GroupBy(x => x.SessionId!.Value)
            .Where(group => group.Last().Type is AuditEventType.ConnectionAttempted or AuditEventType.ConnectionSucceeded)
            .Select(group => group.Key)
            .ToArray();
        foreach (var sessionId in interrupted)
        {
            db.AuditEvents.Add(new AuditEvent
            {
                Type = AuditEventType.RecoveryCompleted,
                SessionId = sessionId,
                Outcome = "disconnected-after-restart",
                RedactedDetail = "The previous application process ended before the session recorded a terminal state."
            });
        }
        if (interrupted.Length > 0) await db.SaveChangesAsync(cancellationToken);
        return interrupted.Length;
    }
}

public static class SecretRedactor
{
    private static readonly string[] Markers = ["password", "passwd", "pwd", "token", "secret", "authorization"];

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var lines = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.Select(line =>
        {
            var marker = Markers.FirstOrDefault(candidate => line.Contains(candidate, StringComparison.OrdinalIgnoreCase));
            if (marker is null) return line;
            var separator = line.IndexOfAny(['=', ':']);
            return separator >= 0 ? $"{line[..(separator + 1)]}[REDACTED]" : "[REDACTED SENSITIVE FIELD]";
        }));
    }
}
