using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NextRdp.Platform.Infrastructure;

namespace NextRdp.ControlPlane;

public sealed partial class OutboxWorker(IServiceScopeFactory scopes, ILogger<OutboxWorker> logger, TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await DispatchBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { LogBatchFailure(logger, exception); }
        }
    }

    private async Task DispatchBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var now = timeProvider.GetUtcNow();
        var messages = await db.OutboxMessages.Where(message => message.ProcessedAtUtc == null && (message.NextAttemptAtUtc == null || message.NextAttemptAtUtc <= now))
            .OrderBy(message => message.OccurredAtUtc).ThenBy(message => message.Id).Take(50).ToListAsync(cancellationToken);
        foreach (var message in messages)
        {
            try
            {
                LogEventDispatched(logger, message.Type, message.Id);
                message.ProcessedAtUtc = now;
                message.LastErrorCode = string.Empty;
            }
            catch (Exception exception)
            {
                message.Attempts++;
                message.LastErrorCode = exception.GetType().Name.Length > 100 ? "dispatch_error" : exception.GetType().Name;
                message.NextAttemptAtUtc = now.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(message.Attempts, 8))));
            }
        }
        if (messages.Count > 0) await db.SaveChangesAsync(cancellationToken);
    }

    [LoggerMessage(EventId = 2101, Level = LogLevel.Error, Message = "The transactional outbox batch failed and will be retried.")]
    private static partial void LogBatchFailure(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Information, Message = "Dispatched durable platform event {EventType} with identifier {EventId}.")]
    private static partial void LogEventDispatched(ILogger logger, string eventType, Guid eventId);
}

public sealed class OutboxBacklogHealthCheck(IServiceScopeFactory scopes, TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var oldest = await db.OutboxMessages.AsNoTracking().Where(message => message.ProcessedAtUtc == null)
            .OrderBy(message => message.OccurredAtUtc).Select(message => (DateTimeOffset?)message.OccurredAtUtc).FirstOrDefaultAsync(cancellationToken);
        if (oldest is null) return HealthCheckResult.Healthy("Transactional outbox is current.");
        var age = timeProvider.GetUtcNow() - oldest.Value;
        return age > TimeSpan.FromMinutes(5)
            ? HealthCheckResult.Degraded("Transactional outbox has messages older than five minutes.")
            : HealthCheckResult.Healthy("Transactional outbox has a bounded pending batch.");
    }
}
