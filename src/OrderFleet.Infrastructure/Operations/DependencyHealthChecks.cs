using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OrderFleet.Infrastructure.Messaging;
using OrderFleet.Infrastructure.Persistence;
using OrderFleet.Infrastructure.Recovery;

namespace OrderFleet.Infrastructure.Operations;

public sealed class DatabaseHealthCheck(IDbContextFactory<OrderFleetDbContext> contexts) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using var database = await contexts.CreateDbContextAsync(timeout.Token);
            if (!await database.Database.CanConnectAsync(timeout.Token)
                || (await database.Database.GetPendingMigrationsAsync(timeout.Token)).Any()) return HealthCheckResult.Unhealthy("Database unavailable or schema pending.");
            return HealthCheckResult.Healthy();
        }
        catch { return HealthCheckResult.Unhealthy("Database unavailable."); }
    }
}

public sealed class BrokerHealthCheck(IOptions<RabbitOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled) return HealthCheckResult.Unhealthy("Delivery paused.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using var session = await RabbitSession.OpenAsync(options.Value, timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch { return HealthCheckResult.Unhealthy("Broker unavailable."); }
    }
}

public sealed class WorkersHealthCheck(IOptions<RabbitOptions> messaging, IOptions<RecoveryOptions> recovery, WorkerProgress workers) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(messaging.Value.Enabled && recovery.Value.Enabled && workers.Snapshot().All(x => x.Ready) ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Required processing paused."));
}
