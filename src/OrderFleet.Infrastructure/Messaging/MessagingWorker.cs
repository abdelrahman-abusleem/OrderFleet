using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFleet.Infrastructure.Operations;

namespace OrderFleet.Infrastructure.Messaging;

public sealed class MessagingWorker(OutboxRelay relay, RabbitConsumer consumer, IOptions<RabbitOptions> options,
    ILogger<MessagingWorker> logger, WorkerProgress progress) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            foreach (var name in new[] { "outbox", "payments", "dispatch", "realtime", "financial" }) progress.Disable(name);
            return Task.CompletedTask;
        }
        return Task.WhenAll(PublishLoopAsync(stoppingToken), ConsumeLoopAsync(consumer.RunPaymentsAsync, "payments", stoppingToken),
            ConsumeLoopAsync(consumer.RunDispatchAsync, "dispatch", stoppingToken),
            ConsumeLoopAsync(consumer.RunRealtimeAsync, "realtime", stoppingToken),
            ConsumeLoopAsync(consumer.RunFinancialAsync, "financial", stoppingToken));
    }

    private async Task PublishLoopAsync(CancellationToken ct)
    {
        progress.Begin("outbox");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    progress.BeginWork("outbox");
                    var count = await relay.RunBatchAsync(ct);
                    progress.Success("outbox");
                    if (count == 0) await Task.Delay(options.Value.PollMilliseconds, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    progress.Failure("outbox");
                    logger.LogWarning("Outbox worker unavailable {FailureType}", error.GetType().Name);
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }
        }
        finally { progress.Stop("outbox"); }
    }

    private async Task ConsumeLoopAsync(Func<CancellationToken, Task> run, string consumerName, CancellationToken ct)
    {
        progress.Begin(consumerName);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await run(ct); throw new IOException("Consumer terminated unexpectedly."); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception error) { progress.Failure(consumerName); logger.LogWarning("{Consumer} consumer unavailable {FailureType}", consumerName, error.GetType().Name); }
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
        finally { progress.Stop(consumerName); }
    }
}
