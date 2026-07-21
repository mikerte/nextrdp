using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Data.Sqlite;

namespace NextRdp.Infrastructure;

public static class LocalDatabaseInitializer
{
    private static readonly IReadOnlyDictionary<string, string[]> RequiredLegacyColumns = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["Connections"] = ["Id", "FolderId", "Name", "Host", "Port", "UsernameHint", "Environment", "Notes", "IsFavorite", "LastConnectedAtUtc", "Tags", "Settings", "CreatedAtUtc", "UpdatedAtUtc"],
        ["Folders"] = ["Id", "ParentId", "Name", "SortOrder"],
        ["AuditEvents"] = ["Id", "Type", "OccurredAtUtc", "ConnectionId", "SessionId", "Outcome", "RedactedDetail", "CorrelationId"]
    };

    public static async Task InitializeAsync(IDbContextFactory<NextRdpDbContext> factory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var migrations = db.Database.GetMigrations().ToArray();
        if (migrations.Length == 0) throw new InvalidOperationException("The local database has no compiled schema migrations.");
        var initialMigration = migrations[0];

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var tables = await ReadNamesAsync(db.Database.GetDbConnection(),
                "SELECT name FROM sqlite_master WHERE type='table'", cancellationToken);
            var existingDomainTables = RequiredLegacyColumns.Keys.Where(tables.Contains).ToArray();
            if (existingDomainTables.Length is > 0 && existingDomainTables.Length < RequiredLegacyColumns.Count)
                throw new InvalidOperationException("The local inventory has a partial schema and was left unchanged. Restore a known-good database or create a redacted support package for diagnosis.");

            if (existingDomainTables.Length == RequiredLegacyColumns.Count)
            {
                foreach (var (table, requiredColumns) in RequiredLegacyColumns)
                {
                    var columns = await ReadNamesAsync(db.Database.GetDbConnection(), $"PRAGMA table_info(\"{table}\")", cancellationToken, nameOrdinal: 1);
                    var missing = requiredColumns.Where(column => !columns.Contains(column)).ToArray();
                    if (missing.Length > 0)
                        throw new InvalidOperationException($"The local inventory table {table} is missing required columns and was left unchanged.");
                }

                var initialMigrationApplied = tables.Contains("__EFMigrationsHistory") &&
                    await IsMigrationAppliedAsync(db.Database.GetDbConnection(), initialMigration, cancellationToken);
                if (!initialMigrationApplied)
                {
                    await BackupLegacyDatabaseAsync(db.Database.GetDbConnection(), initialMigration, cancellationToken);
                    await db.Database.ExecuteSqlRawAsync("""
                        CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                            "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                            "ProductVersion" TEXT NOT NULL
                        );
                        """, cancellationToken);
                    await using (var history = db.Database.GetDbConnection().CreateCommand())
                    {
                        history.CommandText = "INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ($migration, $version)";
                        AddParameter(history, "$migration", initialMigration);
                        AddParameter(history, "$version", ProductInfo.GetVersion());
                        await history.ExecuteNonQueryAsync(cancellationToken);
                    }
                    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_AuditEvents_OccurredAtUtc\" ON \"AuditEvents\" (\"OccurredAtUtc\");", cancellationToken);
                    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_Connections_Host_Port\" ON \"Connections\" (\"Host\", \"Port\");", cancellationToken);
                    await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Folders_ParentId_Name\" ON \"Folders\" (\"ParentId\", \"Name\");", cancellationToken);
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        await db.Database.MigrateAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var check = db.Database.GetDbConnection().CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            var result = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQLite reported that the local inventory failed its integrity check. The application will not write to it.");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> IsMigrationAppliedAsync(DbConnection connection, string migration, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = $migration)";
        AddParameter(command, "$migration", migration);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task BackupLegacyDatabaseAsync(DbConnection connection, string initialMigration, CancellationToken cancellationToken)
    {
        if (connection is not SqliteConnection source || string.IsNullOrWhiteSpace(source.DataSource) || source.DataSource == ":memory:") return;
        var sourcePath = Path.GetFullPath(source.DataSource);
        if (!File.Exists(sourcePath)) return;
        var safeMigration = string.Concat(initialMigration.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        var backupPath = $"{sourcePath}.before-{safeMigration}.bak";
        if (File.Exists(backupPath)) return;

        await using var destination = new SqliteConnection($"Data Source={backupPath};Mode=ReadWriteCreate");
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private static async Task<HashSet<string>> ReadNamesAsync(DbConnection connection, string commandText, CancellationToken cancellationToken, int nameOrdinal = 0)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) names.Add(reader.GetString(nameOrdinal));
        return names;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
