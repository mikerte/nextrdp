using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NextRdp.Infrastructure;

public sealed class NextRdpDesignTimeDbContextFactory : IDesignTimeDbContextFactory<NextRdpDbContext>
{
    public NextRdpDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NextRdpDbContext>()
            .UseSqlite("Data Source=nextrdp-design.db")
            .Options;
        return new(options);
    }
}
