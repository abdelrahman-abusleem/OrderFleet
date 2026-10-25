using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderFleet.Infrastructure.Messaging;

public sealed class MessagingWorker(OutboxRelay relay, RabbitConsumer consumer, IOptions<RabbitOptions> options,
    ILogger<MessagingWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => options.Value.Enabled
        ? Task.WhenAll(PublishLoopAsync(stoppingToken), ConsumeLoopAsync(stoppingToken)) : Task.CompletedTask;

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

    private async Task ConsumeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await consumer.RunPaymentsAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Payment consumer unavailable {FailureType}", error.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }
}
