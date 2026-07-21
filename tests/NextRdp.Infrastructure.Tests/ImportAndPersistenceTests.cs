using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NextRdp.Core;
using System.IO.Compression;

namespace NextRdp.Infrastructure.Tests;

public sealed class ImportAndPersistenceTests : IDisposable
{
    private readonly string tempDirectory = Path.Combine(Path.GetTempPath(), $"nextrdp-tests-{Guid.NewGuid():N}");

    public ImportAndPersistenceTests()
    {
        SQLitePCL.Batteries_V2.Init();
        Directory.CreateDirectory(tempDirectory);
    }

    [Fact]
    public async Task RdpImportDiscardsPasswordBlobs()
    {
        var path = Path.Combine(tempDirectory, "safe.rdp");
        await File.WriteAllTextAsync(path, "full address:s:server.example.test:3390\nusername:s:DOMAIN\\operator\npassword 51:b:DEADBEEF\nredirectclipboard:i:1\n");
        var preview = await new FileImportService().PreviewAsync(path);
        var candidate = Assert.Single(preview.Candidates);
        Assert.Equal("server.example.test", candidate.Host);
        Assert.Equal(3390, candidate.Port);
        Assert.Equal(1, preview.SecretFieldsDiscarded);
        Assert.Equal("safe", candidate.Name);
        Assert.DoesNotContain("DEADBEEF", System.Text.Json.JsonSerializer.Serialize(preview), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RdgImportPreservesHierarchyAndDiscardsCredentials()
    {
        var path = Path.Combine(tempDirectory, "safe.rdg");
        const string xml = """
            <RDCMan><file><group><properties><name>Production</name></properties><server><properties><name>prod-01</name><displayName>Database</displayName><password>secret</password></properties></server></group></file></RDCMan>
            """;
        await File.WriteAllTextAsync(path, xml);
        var preview = await new FileImportService().PreviewAsync(path);
        var candidate = Assert.Single(preview.Candidates);
        Assert.Equal(["Production"], candidate.FolderPath);
        Assert.Equal("prod-01", candidate.Host);
        Assert.Equal(1, preview.SecretFieldsDiscarded);
    }

    [Fact]
    public async Task RdpImportSupportsBracketedIpv6AndReconnectSettings()
    {
        var path = Path.Combine(tempDirectory, "ipv6.rdp");
        await File.WriteAllTextAsync(path, "full address:s:[2001:db8::10]:3391\nautoreconnection enabled:i:1\nautoreconnect max retries:i:9\n");
        var candidate = Assert.Single((await new FileImportService().PreviewAsync(path)).Candidates);
        Assert.Equal("2001:db8::10", candidate.Host);
        Assert.Equal(3391, candidate.Port);
        Assert.Equal(9, candidate.Settings.ReconnectAttempts);
        Assert.Empty(candidate.Warnings);
    }

    [Theory]
    [InlineData("[2001:db8::10]suffix")]
    [InlineData("[2001:db8::10]:bad")]
    [InlineData("[2001:db8::10]:")]
    [InlineData("[server.example.test]:3389")]
    [InlineData("[192.0.2.10]:3389")]
    public async Task RdpImportRejectsMalformedBracketedIpv6Suffixes(string address)
    {
        var path = Path.Combine(tempDirectory, $"malformed-ipv6-{Guid.CreateVersion7():N}.rdp");
        await File.WriteAllTextAsync(path, $"full address:s:{address}\n");

        var candidate = Assert.Single((await new FileImportService().PreviewAsync(path)).Candidates);

        Assert.Contains(candidate.Warnings, warning => warning.Contains("valid hostname", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RdpImportPreservesCustomPortNlaAndRedirectionSemantics()
    {
        var path = Path.Combine(tempDirectory, "settings.rdp");
        await File.WriteAllTextAsync(path, """
            full address:s:server.example.test
            server port:i:3395
            domain:s:CONTOSO
            username:s:operator
            enablecredsspsupport:i:0
            drivestoredirect:s:C:\;E:\;
            redirectprinters:i:1
            redirectclipboard:i:0
            audiomode:i:1
            """);

        var candidate = Assert.Single((await new FileImportService().PreviewAsync(path)).Candidates);

        Assert.Equal(3395, candidate.Port);
        Assert.Equal("CONTOSO\\operator", candidate.UsernameHint);
        Assert.False(candidate.Settings.RequireNetworkLevelAuthentication);
        Assert.True(candidate.Settings.RedirectDrives);
        Assert.True(candidate.Settings.RedirectPrinters);
        Assert.False(candidate.Settings.RedirectClipboard);
        Assert.False(candidate.Settings.RedirectAudio);
        Assert.Empty(candidate.Warnings);
    }

    [Fact]
    public async Task RdpImportUsesEmbeddedPortAndLegacyDriveFlagWhenPresent()
    {
        var path = Path.Combine(tempDirectory, "legacy-settings.rdp");
        await File.WriteAllTextAsync(path, """
            full address:s:server.example.test:3396
            server port:i:3395
            username:s:operator@example.test
            redirectdrives:i:1
            audiomode:i:0
            """);

        var candidate = Assert.Single((await new FileImportService().PreviewAsync(path)).Candidates);

        Assert.Equal(3396, candidate.Port);
        Assert.Equal("operator@example.test", candidate.UsernameHint);
        Assert.True(candidate.Settings.RequireNetworkLevelAuthentication);
        Assert.True(candidate.Settings.RedirectDrives);
        Assert.True(candidate.Settings.RedirectAudio);
        Assert.Empty(candidate.Warnings);
    }

    [Fact]
    public async Task RdpImportIgnoresRecognizedSettingsWithTheWrongDeclaredType()
    {
        var path = Path.Combine(tempDirectory, "wrong-types.rdp");
        await File.WriteAllTextAsync(path, "full address:s:typed.example.test\nserver port:s:3399\nredirectclipboard:s:0\n");

        var candidate = Assert.Single((await new FileImportService().PreviewAsync(path)).Candidates);

        Assert.Equal(3389, candidate.Port);
        Assert.True(candidate.Settings.RedirectClipboard);

        var invalidAddressPath = Path.Combine(tempDirectory, "wrong-address-type.rdp");
        await File.WriteAllTextAsync(invalidAddressPath, "full address:i:typed.example.test\n");
        var invalidAddress = await new FileImportService().PreviewAsync(invalidAddressPath);
        Assert.Empty(invalidAddress.Candidates);
        Assert.Contains(invalidAddress.Errors, error => error.Contains("no valid full address", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportMarksInvalidCandidateBeforePersistence()
    {
        var path = Path.Combine(tempDirectory, "invalid.rdp");
        await File.WriteAllTextAsync(path, "full address:s:bad host name:3389\n");
        var candidate = Assert.Single((await new FileImportService().PreviewAsync(path)).Candidates);
        Assert.Contains(candidate.Warnings, warning => warning.Contains("valid hostname", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RdgImportRejectsDtdAndExternalEntityContent()
    {
        var path = Path.Combine(tempDirectory, "entity.rdg");
        await File.WriteAllTextAsync(path, "<!DOCTYPE x [<!ENTITY leak SYSTEM 'file:///windows/win.ini'>]><RDCMan>&leak;</RDCMan>");
        var preview = await new FileImportService().PreviewAsync(path);
        Assert.Empty(preview.Candidates);
        Assert.Contains(preview.Errors, error => error.Contains("prohibited XML", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RdgImportRejectsXmlThatIsNotAnRdcManDocument()
    {
        var path = Path.Combine(tempDirectory, "wrong-root.rdg");
        await File.WriteAllTextAsync(path, "<inventory><server><properties><name>unexpected.example.test</name></properties></server></inventory>");

        var preview = await new FileImportService().PreviewAsync(path);

        Assert.Empty(preview.Candidates);
        Assert.Contains(preview.Errors, error => error.Contains("root must be RDCMan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RdgImportRejectsExcessiveXmlNestingBeforeRecursiveTraversal()
    {
        var path = Path.Combine(tempDirectory, "deep.rdg");
        var opening = string.Concat(Enumerable.Repeat("<group>", 70));
        var closing = string.Concat(Enumerable.Repeat("</group>", 70));
        await File.WriteAllTextAsync(path, $"<RDCMan>{opening}{closing}</RDCMan>");

        var preview = await new FileImportService().PreviewAsync(path);

        Assert.Empty(preview.Candidates);
        Assert.Contains(preview.Errors, error => error.Contains("nesting depth", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RdgImportMarksUnsafeFolderNamesAndExcessiveFolderDepthBeforePersistence()
    {
        var path = Path.Combine(tempDirectory, "unsafe-folders.rdg");
        var groups = string.Concat(Enumerable.Range(0, 33).Select(index => $"<group><properties><name>{(index == 0 ? "bad/folder" : $"level-{index}")}</name></properties>"));
        var closing = string.Concat(Enumerable.Repeat("</group>", 33));
        await File.WriteAllTextAsync(path, $"<RDCMan>{groups}<server><properties><name>safe-host.example.test</name></properties></server>{closing}</RDCMan>");

        var candidate = Assert.Single((await new FileImportService().PreviewAsync(path)).Candidates);

        Assert.Contains(candidate.Warnings, warning => warning.Contains("path separators", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidate.Warnings, warning => warning.Contains("32 levels", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RdgImportRejectsCandidateFlood()
    {
        var path = Path.Combine(tempDirectory, "candidate-flood.rdg");
        var servers = string.Concat(Enumerable.Range(0, 2001).Select(index => $"<server><properties><name>host-{index}.example.test</name></properties></server>"));
        await File.WriteAllTextAsync(path, $"<RDCMan>{servers}</RDCMan>");

        var preview = await new FileImportService().PreviewAsync(path);

        Assert.Empty(preview.Candidates);
        Assert.Contains(preview.Errors, error => error.Contains("2000 per import", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportRejectsFilesOverThePhaseOneLimit()
    {
        var path = Path.Combine(tempDirectory, "oversized.rdp");
        await File.WriteAllBytesAsync(path, new byte[2 * 1024 * 1024 + 1]);
        var preview = await new FileImportService().PreviewAsync(path);
        Assert.Empty(preview.Candidates);
        Assert.Contains(preview.Errors, error => error.Contains("2 MB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RdpMalformedLineFuzzNeverReturnsSecretValues()
    {
        var random = new Random(12345);
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var path = Path.Combine(tempDirectory, $"fuzz-{iteration}.rdp");
            var noise = Convert.ToHexString(Enumerable.Range(0, 64).Select(_ => (byte)random.Next(0, 256)).ToArray());
            await File.WriteAllTextAsync(path, $"full address:s:host-{iteration}.example.test\npassword 51:b:{noise}\ntoken:s:{noise}\n{noise}\n");
            var preview = await new FileImportService().PreviewAsync(path);
            var serialized = System.Text.Json.JsonSerializer.Serialize(preview);
            Assert.DoesNotContain(noise, serialized, StringComparison.Ordinal);
            Assert.Equal(2, preview.SecretFieldsDiscarded);
        }
    }

    [Fact]
    public async Task RdgGeneratedFuzzNeverReturnsSecretValuesOrThrows()
    {
        var random = new Random(67890);
        var importer = new FileImportService();
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var path = Path.Combine(tempDirectory, $"fuzz-{iteration}.rdg");
            var secret = Convert.ToHexString(Enumerable.Range(0, 48).Select(_ => (byte)random.Next(0, 256)).ToArray());
            var document = (iteration % 4) switch
            {
                0 => $"<RDCMan><file><server><properties><name>host-{iteration}.example.test</name><password>{secret}</password></properties></server></file></RDCMan>",
                1 => $"<RDCMan><group><properties><name>group-{iteration}</name><credentials>{secret}</credentials></properties>",
                2 => $"<!DOCTYPE x [<!ENTITY injected '{secret}'>]><RDCMan>&injected;</RDCMan>",
                _ => $"<RDCMan><file><server><properties><name>bad host {iteration}</name><token>{secret}</token></properties></server></file></RDCMan>"
            };
            await File.WriteAllTextAsync(path, document);

            var preview = await importer.PreviewAsync(path);
            var serialized = System.Text.Json.JsonSerializer.Serialize(preview);

            Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
            if (iteration % 4 is 0 or 3) Assert.Equal(1, preview.SecretFieldsDiscarded);
        }
    }

    [Fact]
    public async Task ImportSupportsBomEncodedUtf16RdpAndRdgFiles()
    {
        var rdpPath = Path.Combine(tempDirectory, "utf16.rdp");
        await File.WriteAllTextAsync(
            rdpPath,
            "full address:s:utf16-rdp.example.test\r\npassword 51:b:0011223344556677\r\n",
            System.Text.Encoding.Unicode);
        var rdpPreview = await new FileImportService().PreviewAsync(rdpPath);
        Assert.Equal("utf16-rdp.example.test", Assert.Single(rdpPreview.Candidates).Host);
        Assert.Equal(1, rdpPreview.SecretFieldsDiscarded);

        var rdgPath = Path.Combine(tempDirectory, "utf16.rdg");
        await File.WriteAllTextAsync(
            rdgPath,
            "<?xml version=\"1.0\" encoding=\"utf-16\"?><RDCMan><file><server><properties><name>utf16-rdg.example.test</name></properties></server></file></RDCMan>",
            System.Text.Encoding.Unicode);
        var rdgPreview = await new FileImportService().PreviewAsync(rdgPath);
        Assert.Equal("utf16-rdg.example.test", Assert.Single(rdgPreview.Candidates).Host);
    }

    [Fact]
    public async Task SqliteRepositoryRoundTripsConnectionWithoutPasswords()
    {
        SQLitePCL.Batteries_V2.Init();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<NextRdpDbContext>().UseSqlite(connection).Options;
        await using (var setup = new NextRdpDbContext(options)) await setup.Database.EnsureCreatedAsync();
        var factory = new TestFactory(options);
        var repository = new SqliteConnectionRepository(factory);
        var profile = new ConnectionProfile
        {
            Name = "Test",
            Host = "test.example",
            Tags = ["sql", "lab"],
            Settings = new RdpSettings { ShowProductionWatermark = false }
        };
        await repository.SaveAsync(profile);
        var saved = Assert.Single(await repository.ListAsync());
        Assert.Equal(profile.Id, saved.Id);
        Assert.Equal(["sql", "lab"], saved.Tags);
        Assert.False(saved.Settings.ShowProductionWatermark);
    }

    [Fact]
    public async Task SqliteRepositoryRejectsAConnectionAssignedToAMissingFolder()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<NextRdpDbContext>().UseSqlite(connection).Options;
        await using (var setup = new NextRdpDbContext(options)) await setup.Database.EnsureCreatedAsync();
        var repository = new SqliteConnectionRepository(new TestFactory(options));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(new ConnectionProfile
        {
            Name = "Orphaned",
            Host = "orphaned.example.test",
            FolderId = Guid.CreateVersion7()
        }));

        Assert.Contains("folder no longer exists", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await repository.ListAsync());
    }

    [Fact]
    public async Task DatabaseInitializerAppliesCompiledMigrationToFreshDatabase()
    {
        var path = Path.Combine(tempDirectory, "migrated.db");
        var options = new DbContextOptionsBuilder<NextRdpDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new TestFactory(options);

        await LocalDatabaseInitializer.InitializeAsync(factory);

        await using var db = factory.CreateDbContext();
        Assert.Contains("20260718103725_InitialLocalSchema", await db.Database.GetAppliedMigrationsAsync());
        Assert.Contains("20260718145055_EnforceAppendOnlyAudit", await db.Database.GetAppliedMigrationsAsync());
        await LocalDatabaseInitializer.InitializeAsync(factory);
        Assert.False(File.Exists($"{path}.before-20260718103725_InitialLocalSchema.bak"));
        await new SqliteConnectionRepository(factory).SaveAsync(new ConnectionProfile { Name = "Migrated", Host = "migrated.example.test" });
        Assert.Single(await new SqliteConnectionRepository(factory).ListAsync());
    }

    [Fact]
    public async Task DatabaseInitializerBaselinesLegacyEnsureCreatedDatabaseWithoutDataLoss()
    {
        var path = Path.Combine(tempDirectory, "legacy.db");
        var options = new DbContextOptionsBuilder<NextRdpDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new TestFactory(options);
        await using (var legacy = new NextRdpDbContext(options)) await legacy.Database.EnsureCreatedAsync();
        var repository = new SqliteConnectionRepository(factory);
        var profile = new ConnectionProfile { Name = "Legacy", Host = "legacy.example.test" };
        await repository.SaveAsync(profile);

        await LocalDatabaseInitializer.InitializeAsync(factory);

        Assert.Equal(profile.Id, Assert.Single(await repository.ListAsync()).Id);
        await using var verified = new NextRdpDbContext(options);
        Assert.Contains("20260718103725_InitialLocalSchema", await verified.Database.GetAppliedMigrationsAsync());
        Assert.Contains("20260718145055_EnforceAppendOnlyAudit", await verified.Database.GetAppliedMigrationsAsync());
        Assert.True(File.Exists($"{path}.before-20260718103725_InitialLocalSchema.bak"));
    }

    [Fact]
    public async Task DatabaseMigrationEnforcesAppendOnlyAuditEvents()
    {
        var path = Path.Combine(tempDirectory, "append-only.db");
        var options = new DbContextOptionsBuilder<NextRdpDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new TestFactory(options);
        await LocalDatabaseInitializer.InitializeAsync(factory);
        var store = new SqliteAuditStore(factory);
        var audit = new AuditEvent { Type = AuditEventType.DiagnosticExecuted, Outcome = "success" };
        await store.WriteAsync(audit);

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE AuditEvents SET Outcome = 'changed' WHERE Id = $id";
        update.Parameters.AddWithValue("$id", audit.Id);
        var updateError = await Assert.ThrowsAsync<SqliteException>(() => update.ExecuteNonQueryAsync());
        Assert.Contains("append-only", updateError.Message, StringComparison.OrdinalIgnoreCase);

        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM AuditEvents WHERE Id = $id";
        delete.Parameters.AddWithValue("$id", audit.Id);
        var deleteError = await Assert.ThrowsAsync<SqliteException>(() => delete.ExecuteNonQueryAsync());
        Assert.Contains("append-only", deleteError.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("success", Assert.Single(await store.ListRecentAsync()).Outcome);
    }

    [Fact]
    public async Task DatabaseInitializerLeavesPartialLegacySchemaUnchanged()
    {
        var path = Path.Combine(tempDirectory, "partial.db");
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Connections (Id TEXT NOT NULL PRIMARY KEY)";
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<NextRdpDbContext>().UseSqlite($"Data Source={path}").Options;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => LocalDatabaseInitializer.InitializeAsync(new TestFactory(options)));

        Assert.Contains("partial schema", error.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = new SqliteConnection($"Data Source={path}");
        await verify.OpenAsync();
        await using var tables = verify.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Folders', 'AuditEvents', '__EFMigrationsHistory')";
        Assert.Equal(0L, Convert.ToInt64(await tables.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void RedactorRemovesSensitiveLines()
    {
        var redacted = SecretRedactor.Redact("host=server\npassword=hunter2\nresult=failed");
        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains("host=server", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FolderDeletionProtectsNestedFoldersAndConnections()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<NextRdpDbContext>().UseSqlite(connection).Options;
        await using (var setup = new NextRdpDbContext(options)) await setup.Database.EnsureCreatedAsync();
        var repository = new SqliteConnectionRepository(new TestFactory(options));
        var parent = new ConnectionFolder { Name = "Parent" };
        var child = new ConnectionFolder { Name = "Child", ParentId = parent.Id };
        await repository.SaveFolderAsync(parent);
        await repository.SaveFolderAsync(child);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveFolderAsync(new ConnectionFolder { Name = "bad/folder" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveFolderAsync(new ConnectionFolder { Name = "child", ParentId = parent.Id }));
        parent.ParentId = child.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveFolderAsync(parent));
        parent.ParentId = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.DeleteFolderAsync(parent.Id));
        await repository.DeleteFolderAsync(child.Id);
        await repository.DeleteFolderAsync(parent.Id);
        Assert.Empty(await repository.ListFoldersAsync());
    }

    [Fact]
    public async Task FolderRepositoryRejectsHierarchyBeyondTheDomainLimit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<NextRdpDbContext>().UseSqlite(connection).Options;
        await using (var setup = new NextRdpDbContext(options)) await setup.Database.EnsureCreatedAsync();
        var repository = new SqliteConnectionRepository(new TestFactory(options));
        Guid? parentId = null;
        for (var depth = 1; depth <= ConnectionFolder.MaximumDepth; depth++)
        {
            var folder = new ConnectionFolder { Name = $"Level {depth}", ParentId = parentId };
            await repository.SaveFolderAsync(folder);
            parentId = folder.Id;
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveFolderAsync(
            new ConnectionFolder { Name = "Too deep", ParentId = parentId }));

        Assert.Contains("32 levels", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ConnectionFolder.MaximumDepth, (await repository.ListFoldersAsync()).Count);
    }

    [Fact]
    public async Task AuditRecoveryClosesOnlyInterruptedSessions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<NextRdpDbContext>().UseSqlite(connection).Options;
        await using (var setup = new NextRdpDbContext(options)) await setup.Database.EnsureCreatedAsync();
        var store = new SqliteAuditStore(new TestFactory(options));
        var interruptedId = Guid.CreateVersion7();
        var completedId = Guid.CreateVersion7();
        await store.WriteAsync(new AuditEvent { Type = AuditEventType.ConnectionSucceeded, SessionId = interruptedId });
        await store.WriteAsync(new AuditEvent { Type = AuditEventType.ConnectionSucceeded, SessionId = completedId });
        await store.WriteAsync(new AuditEvent { Type = AuditEventType.SessionDisconnected, SessionId = completedId });
        Assert.Equal(1, await store.CloseInterruptedSessionsAsync());
        Assert.Equal(0, await store.CloseInterruptedSessionsAsync());
        Assert.Contains(await store.ListRecentAsync(), x => x.SessionId == interruptedId && x.Type == AuditEventType.RecoveryCompleted);
    }

    [Fact]
    public async Task ExportsContainNoCredentialMaterialAndSupportPackageRedactsTargets()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<NextRdpDbContext>().UseSqlite(connection).Options;
        await using (var setup = new NextRdpDbContext(options)) await setup.Database.EnsureCreatedAsync();
        var factory = new TestFactory(options);
        var repository = new SqliteConnectionRepository(factory);
        var store = new SqliteAuditStore(factory);
        var profile = new ConnectionProfile
        {
            Name = "Sensitive production label",
            Host = "secret-host.internal",
            UsernameHint = "DOMAIN\\private-user",
            Notes = "Private runbook text"
        };
        await repository.SaveAsync(profile);
        await store.WriteAsync(new AuditEvent { Type = AuditEventType.ConnectionAttempted, ConnectionId = profile.Id, RedactedDetail = "target=secret-host.internal:3389 password=hunter2" });
        var service = new LocalExportService(repository, store);
        var inventoryPath = Path.Combine(tempDirectory, "inventory.json");
        var supportPath = Path.Combine(tempDirectory, "support.zip");
        await service.ExportInventoryAsync(inventoryPath);
        await service.CreateRedactedSupportPackageAsync(supportPath);

        var inventory = await File.ReadAllTextAsync(inventoryPath);
        Assert.DoesNotContain("hunter2", inventory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"password\":", inventory, StringComparison.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(supportPath);
        using var reader = new StreamReader(Assert.Single(archive.Entries, x => x.FullName == "support.json").Open());
        var support = await reader.ReadToEndAsync();
        Assert.DoesNotContain("secret-host", support, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-user", support, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Private runbook", support, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", support, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target-001", support, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(tempDirectory, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private sealed class TestFactory(DbContextOptions<NextRdpDbContext> options) : IDbContextFactory<NextRdpDbContext>
    {
        public NextRdpDbContext CreateDbContext() => new(options);
    }
}
