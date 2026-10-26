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
    public OperationalMetrics(WorkerProgress workers, BrokerQueueObserver broker)
    {
        meter.CreateObservableGauge<long>("orderfleet.outbox.pending", () => Read(x => x.OutboxPending));
        meter.CreateObservableGauge<long>("orderfleet.outbox.dead", () => Read(x => x.OutboxDead));
        meter.CreateObservableGauge<double>("orderfleet.outbox.oldest_lag", () =>
            Volatile.Read(ref snapshot) is { } current ? [new Measurement<double>(current.OldestOutboxLagSeconds)] : Array.Empty<Measurement<double>>(), "s");
        meter.CreateObservableGauge<long>("orderfleet.dispatch.pending", () => Read(x => x.DispatchPending));
        meter.CreateObservableGauge<long>("orderfleet.dispatch.exhausted", () => Read(x => x.DispatchExhausted));
        meter.CreateObservableGauge<long>("orderfleet.payments.uncertain", () => Read(x => x.UncertainPayments));
        meter.CreateObservableGauge<long>("orderfleet.recovery.dead", () => Read(x => x.RecoveryDead));
        meter.CreateObservableGauge<long>("orderfleet.recovery.pending", () => Read(x => x.RecoveryPending));
        meter.CreateObservableGauge<double>("orderfleet.worker.progress_age", () => workers.Snapshot().Select(x =>
            new Measurement<double>(x.ProgressAgeSeconds, new KeyValuePair<string, object?>("worker", x.Worker))), "s");
        meter.CreateObservableGauge<int>("orderfleet.worker.ready", () => workers.Snapshot().Select(x =>
            new Measurement<int>(x.Ready ? 1 : 0, new KeyValuePair<string, object?>("worker", x.Worker))));
        meter.CreateObservableGauge<long>("orderfleet.broker.ready_messages", () => broker.Snapshot().Select(x =>
            new Measurement<long>(x.ReadyMessages, new("consumer", x.Consumer), new("queue.kind", x.Kind))));
    }
    public void Update(OperationsSummary current) => Volatile.Write(ref snapshot, current);
    public void Dispose() => meter.Dispose();
    private IEnumerable<Measurement<long>> Read(Func<OperationsSummary, long> select) =>
        Volatile.Read(ref snapshot) is { } current ? [new(select(current))] : Array.Empty<Measurement<long>>();
}

public sealed class OperationalMetricSampler(IServiceScopeFactory scopes, OperationalMetrics metrics, IClock clock, BrokerQueueObserver broker)
{
    public async Task CollectAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        metrics.Update(await scope.ServiceProvider.GetRequiredService<IOperationsStore>().SummaryAsync(clock.UtcNow, ct));
        await broker.CollectAsync(ct);
    }
}

public sealed class OperationalMetricsWorker(OperationalMetricSampler sampler, ILogger<OperationalMetricsWorker> logger, WorkerProgress progress) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        progress.Begin("operations");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { progress.BeginWork("operations"); await sampler.CollectAsync(stoppingToken); progress.Success("operations"); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) { progress.Failure("operations"); logger.LogWarning("Operational metrics unavailable {FailureType}", error.GetType().Name); }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        finally { progress.Stop("operations"); }
    }
}
