using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderFleet.Infrastructure.Messaging;

public sealed class MessagingWorker(OutboxRelay relay, RabbitConsumer consumer, IOptions<RabbitOptions> options,
    ILogger<MessagingWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => options.Value.Enabled
        ? Task.WhenAll(PublishLoopAsync(stoppingToken), ConsumeLoopAsync(consumer.RunPaymentsAsync, "Payment", stoppingToken),
            ConsumeLoopAsync(consumer.RunDispatchAsync, "Dispatch", stoppingToken)) : Task.CompletedTask;

    private async Task PublishLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var count = await relay.RunBatchAsync(ct);
                if (count == 0) await Task.Delay(options.Value.PollMilliseconds, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning("Outbox worker unavailable {FailureType}", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task ConsumeLoopAsync(Func<CancellationToken, Task> run, string consumerName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await run(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("{Consumer} consumer unavailable {FailureType}", consumerName, error.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
}
