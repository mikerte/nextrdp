using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NextRdp.Platform.Domain;

namespace NextRdp.Platform.Infrastructure.Tests;

public sealed class PlatformDbContextTests
{
    [Fact]
    public void ModelUsesDedicatedSchemaAndRequiredScopedIndexes()
    {
        using var context = CreateNpgsqlContext();

        Assert.All(context.Model.GetEntityTypes().Where(type => !type.IsOwned()), type => Assert.Equal("nextrdp", type.GetSchema()));
        AssertScopedIndex<Workspace>(context, nameof(Workspace.OrganizationId));
        AssertScopedIndex<ManagedFolder>(context, nameof(ManagedFolder.OrganizationId), nameof(ManagedFolder.WorkspaceId));
        AssertScopedIndex<ManagedConnection>(context, nameof(ManagedConnection.OrganizationId), nameof(ManagedConnection.WorkspaceId));
        AssertScopedIndex<PlatformAuditEvent>(context, nameof(PlatformAuditEvent.OrganizationId), nameof(PlatformAuditEvent.WorkspaceId));
        AssertScopedIndex<SyncChange>(context, nameof(SyncChange.OrganizationId), nameof(SyncChange.WorkspaceId));
    }

    [Fact]
    public void MutableSharedResourcesHaveConcurrencyTokens()
    {
        using var context = CreateNpgsqlContext();

        AssertConcurrencyToken<Organization>(context, nameof(Organization.Revision));
        AssertConcurrencyToken<Workspace>(context, nameof(Workspace.Revision));
        AssertConcurrencyToken<ManagedFolder>(context, nameof(ManagedFolder.Revision));
        AssertConcurrencyToken<ManagedConnection>(context, nameof(ManagedConnection.Revision));
        AssertConcurrencyToken<ConfigurationDraft>(context, nameof(ConfigurationDraft.Revision));
    }

    [Fact]
    public void GeneratedPostgresSchemaUsesJsonbAndNoCredentialColumns()
    {
        using var context = CreateNpgsqlContext();

        var script = context.Database.GenerateCreateScript();

        Assert.Contains("jsonb", script, StringComparison.Ordinal);
        Assert.Contains("ck_connection_personal_owner", script, StringComparison.Ordinal);
        Assert.DoesNotContain("password", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangeTrackerRejectsAuditUpdateAndDelete()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var context = new PlatformDbContext(options);
        var audit = new PlatformAuditEvent
        {
            OrganizationId = Guid.CreateVersion7(),
            Action = "workspace.created",
            ResourceType = "workspace",
            Outcome = AuditOutcome.Succeeded
        };
        context.AuditEvents.Add(audit);
        await context.SaveChangesAsync();

        audit.Action = "tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        context.Entry(audit).State = EntityState.Unchanged;
        context.AuditEvents.Remove(audit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    private static PlatformDbContext CreateNpgsqlContext()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql("Host=localhost;Database=nextrdp_model;Username=nextrdp;Include Error Detail=false")
            .Options;
        return new PlatformDbContext(options);
    }

    private static void AssertScopedIndex<TEntity>(PlatformDbContext context, params string[] prefix) where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity));
        Assert.NotNull(entityType);
        Assert.Contains(entityType.GetIndexes(), index => index.Properties.Take(prefix.Length).Select(property => property.Name).SequenceEqual(prefix));
    }

    private static void AssertConcurrencyToken<TEntity>(PlatformDbContext context, string propertyName) where TEntity : class
    {
        var property = context.Model.FindEntityType(typeof(TEntity))?.FindProperty(propertyName);
        Assert.NotNull(property);
        Assert.True(property.IsConcurrencyToken);
    }
}
