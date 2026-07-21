using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NextRdp.Core;

namespace NextRdp.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNextRdpLocalInfrastructure(this IServiceCollection services, string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        services.AddDbContextFactory<NextRdpDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<IConnectionRepository, SqliteConnectionRepository>();
        services.AddSingleton<IAuditStore, SqliteAuditStore>();
        services.AddSingleton<IImportService, FileImportService>();
        services.AddSingleton<IConnectivityDiagnostics, ConnectivityDiagnostics>();
        services.AddSingleton<ILocalExportService, LocalExportService>();
        services.AddSingleton<IPolicyDecisionPoint, LocalPolicyDecisionPoint>();
        return services;
    }
}
