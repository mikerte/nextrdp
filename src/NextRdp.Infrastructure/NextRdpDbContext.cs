using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NextRdp.Core;

namespace NextRdp.Infrastructure;

public sealed class NextRdpDbContext(DbContextOptions<NextRdpDbContext> options) : DbContext(options)
{
    public DbSet<ConnectionProfile> Connections => Set<ConnectionProfile>();
    public DbSet<ConnectionFolder> Folders => Set<ConnectionFolder>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var connection = modelBuilder.Entity<ConnectionProfile>();
        connection.HasKey(x => x.Id);
        connection.Property(x => x.Name).HasMaxLength(200).IsRequired();
        connection.Property(x => x.Host).HasMaxLength(255).IsRequired();
        connection.Property(x => x.UsernameHint).HasMaxLength(320);
        connection.Property(x => x.Notes).HasMaxLength(4000);
        connection.Ignore(x => x.SearchableMetadata);
        connection.Property(x => x.Tags).HasConversion(
            value => JsonSerializer.Serialize(value, JsonSerializerOptions.Default),
            value => JsonSerializer.Deserialize<List<string>>(value, JsonSerializerOptions.Default) ?? new List<string>());
        connection.Property(x => x.Settings).HasConversion(
            value => JsonSerializer.Serialize(value, JsonSerializerOptions.Default),
            value => JsonSerializer.Deserialize<RdpSettings>(value, JsonSerializerOptions.Default) ?? new RdpSettings());
        connection.HasIndex(x => new { x.Host, x.Port });

        var folder = modelBuilder.Entity<ConnectionFolder>();
        folder.HasKey(x => x.Id);
        folder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        folder.HasIndex(x => new { x.ParentId, x.Name }).IsUnique();

        var audit = modelBuilder.Entity<AuditEvent>();
        audit.HasKey(x => x.Id);
        audit.Property(x => x.Outcome).HasMaxLength(100);
        audit.Property(x => x.RedactedDetail).HasMaxLength(2000);
        audit.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
        audit.HasIndex(x => x.OccurredAtUtc);
    }
}
