using System.IO.Compression;
using System.Text.Json;
using NextRdp.Core;

namespace NextRdp.Infrastructure;

public sealed class LocalExportService(
    IConnectionRepository repository,
    IAuditStore auditStore) : ILocalExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task ExportInventoryAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var connections = await repository.ListAsync(cancellationToken);
        var folders = await repository.ListFoldersAsync(cancellationToken);
        var document = new
        {
            schemaVersion = 1,
            exportedAtUtc = DateTimeOffset.UtcNow,
            securityNotice = "This export intentionally contains no passwords, password blobs, tokens, or credential references.",
            folders = folders.Select(x => new { x.Id, x.ParentId, x.Name, x.SortOrder }),
            connections = connections.Select(x => new
            {
                x.Id,
                x.FolderId,
                x.Name,
                x.Host,
                x.Port,
                x.UsernameHint,
                environment = x.Environment.ToString(),
                x.Notes,
                x.IsFavorite,
                x.LastConnectedAtUtc,
                x.Tags,
                x.Settings
            })
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document, JsonOptions), cancellationToken);
        await auditStore.WriteAsync(new AuditEvent
        {
            Type = AuditEventType.InventoryExported,
            Outcome = "success",
            RedactedDetail = $"connections={connections.Count} folders={folders.Count}"
        }, cancellationToken);
    }

    public async Task CreateRedactedSupportPackageAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var connections = await repository.ListAsync(cancellationToken);
        var audits = await auditStore.ListRecentAsync(500, cancellationToken);
        var targetMap = connections.Select((connection, index) => (connection.Id, Alias: $"target-{index + 1:D3}"))
            .ToDictionary(x => x.Id, x => x.Alias);
        var supportDocument = new
        {
            schemaVersion = 1,
            generatedAtUtc = DateTimeOffset.UtcNow,
            platform = Environment.OSVersion.VersionString,
            runtime = Environment.Version.ToString(),
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            connectionCount = connections.Count,
            connections = connections.Select(x => new
            {
                target = targetMap[x.Id],
                x.Port,
                environment = x.Environment.ToString(),
                tagCount = x.Tags.Count,
                x.IsFavorite,
                x.Settings
            }),
            recentAudit = audits.Select(x => new
            {
                x.OccurredAtUtc,
                type = x.Type.ToString(),
                target = x.ConnectionId is { } id && targetMap.TryGetValue(id, out var alias) ? alias : null,
                x.Outcome
            })
        };

        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
        {
            {
                var entry = archive.CreateEntry("support.json", CompressionLevel.Optimal);
                await using var stream = entry.Open();
                await JsonSerializer.SerializeAsync(stream, supportDocument, JsonOptions, cancellationToken);
            }
            {
                var readme = archive.CreateEntry("README.txt", CompressionLevel.Optimal);
                await using var readmeStream = new StreamWriter(readme.Open());
                await readmeStream.WriteAsync("NextRDP redacted support package. Hostnames, connection names, usernames, notes, folder names, and credentials are excluded.");
            }
        }

        await auditStore.WriteAsync(new AuditEvent
        {
            Type = AuditEventType.SupportPackageCreated,
            Outcome = "success",
            RedactedDetail = "redacted_package=true"
        }, cancellationToken);
    }
}
