using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NextRdp.Platform.Infrastructure;

namespace NextRdp.ControlPlane;

public static class NextRdpHealthChecks
{
    public static IServiceCollection AddNextRdpHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgresql", tags: ["ready"])
            .AddCheck<OpenFgaHealthCheck>("openfga", tags: ["ready"])
            .AddCheck<OpaHealthCheck>("opa", tags: ["ready"])
            .AddCheck<OutboxBacklogHealthCheck>("outbox", tags: ["ready"]);
        return services;
    }
}

public sealed class PostgresHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            return await dbContext.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("PostgreSQL is reachable.")
                : HealthCheckResult.Unhealthy("PostgreSQL rejected the readiness probe.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.");
        }
    }
}

public sealed class OpenFgaHealthCheck(IHttpClientFactory clients, OpenFgaOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.StoreId)) return HealthCheckResult.Unhealthy("OpenFGA is not initialized.");
        try
        {
            using var client = clients.CreateClient(nameof(OpenFgaHealthCheck));
            client.BaseAddress = options.BaseAddress;
            client.Timeout = TimeSpan.FromSeconds(2);
            using var response = await client.GetAsync("healthz", cancellationToken);
            return response.IsSuccessStatusCode ? HealthCheckResult.Healthy("OpenFGA is ready.") : HealthCheckResult.Unhealthy("OpenFGA readiness failed.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("OpenFGA is unavailable.");
        }
    }
}

public sealed class OpaHealthCheck(IHttpClientFactory clients, OpaOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = clients.CreateClient(nameof(OpaHealthCheck));
            client.BaseAddress = options.BaseAddress;
            client.Timeout = TimeSpan.FromSeconds(2);
            using var response = await client.GetAsync("health", cancellationToken);
            return response.IsSuccessStatusCode ? HealthCheckResult.Healthy("OPA is ready.") : HealthCheckResult.Unhealthy("OPA readiness failed.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("OPA is unavailable.");
        }
    }
}

public static class HealthResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        var payload = new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            durationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 2),
            checks = report.Entries.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString().ToLowerInvariant(),
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 2)
            })
        };
        return JsonSerializer.SerializeAsync(context.Response.Body, payload, cancellationToken: context.RequestAborted);
    }
}
