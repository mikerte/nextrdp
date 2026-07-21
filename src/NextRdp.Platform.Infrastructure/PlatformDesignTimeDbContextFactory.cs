using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NextRdp.Platform.Infrastructure;

public sealed class PlatformDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NEXTRDP_POSTGRES_CONNECTION") ??
            "Host=localhost;Port=5432;Database=nextrdp;Username=nextrdp;Include Error Detail=false";
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "nextrdp"))
            .Options;
        return new PlatformDbContext(options);
    }
}
