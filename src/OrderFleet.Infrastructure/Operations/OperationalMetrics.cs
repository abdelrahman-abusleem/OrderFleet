using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderFleet.Application.Clock;
using OrderFleet.Application.Diagnostics;
using OrderFleet.Application.Operations;

namespace OrderFleet.Infrastructure.Operations;

public sealed class OperationalMetrics : IDisposable
{
    private readonly Meter meter = new(OrderFleetDiagnostics.Name + ".Operations", "1.0.0");
    private OperationsSummary? snapshot;
    public OperationalMetrics()
    {
        meter.CreateObservableGauge<long>("orderfleet.outbox.pending", () => Read(x => x.OutboxPending));
        meter.CreateObservableGauge<long>("orderfleet.outbox.dead", () => Read(x => x.OutboxDead));
        meter.CreateObservableGauge<double>("orderfleet.outbox.oldest_lag", () =>
            Volatile.Read(ref snapshot) is { } current ? [new Measurement<double>(current.OldestOutboxLagSeconds)] : Array.Empty<Measurement<double>>(), "s");
        meter.CreateObservableGauge<long>("orderfleet.dispatch.pending", () => Read(x => x.DispatchPending));
        meter.CreateObservableGauge<long>("orderfleet.dispatch.exhausted", () => Read(x => x.DispatchExhausted));
        meter.CreateObservableGauge<long>("orderfleet.payments.uncertain", () => Read(x => x.UncertainPayments));
        meter.CreateObservableGauge<long>("orderfleet.recovery.dead", () => Read(x => x.RecoveryDead));
    }
    public void Update(OperationsSummary current) => Volatile.Write(ref snapshot, current);
    public void Dispose() => meter.Dispose();
    private IEnumerable<Measurement<long>> Read(Func<OperationsSummary, long> select) =>
        Volatile.Read(ref snapshot) is { } current ? [new(select(current))] : Array.Empty<Measurement<long>>();
}

public sealed class OperationalMetricSampler(IServiceScopeFactory scopes, OperationalMetrics metrics, IClock clock)
{
    public async Task CollectAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        metrics.Update(await scope.ServiceProvider.GetRequiredService<IOperationsStore>().SummaryAsync(clock.UtcNow, ct));
    }
}

public sealed class OperationalMetricsWorker(OperationalMetricSampler sampler, ILogger<OperationalMetricsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await sampler.CollectAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Operational metrics unavailable {FailureType}", error.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
