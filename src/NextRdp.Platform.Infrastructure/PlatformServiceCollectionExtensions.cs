using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NextRdp.Platform.Application;

namespace NextRdp.Platform.Infrastructure;

public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddNextRdpPlatformPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("NextRdp") ??
            throw new InvalidOperationException("ConnectionStrings:NextRdp is required. No in-memory authoritative fallback is available.");

        services.AddDbContext<PlatformDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__ef_migrations_history", "nextrdp");
                npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
            });
            options.UseOpenIddict<Guid>();
        });
        return services;
    }
}
