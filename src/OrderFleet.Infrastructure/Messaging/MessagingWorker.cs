using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFleet.Infrastructure.Operations;
using OrderFleet.Infrastructure.Reliability;
using OrderFleet.Application.Errors;

namespace OrderFleet.Infrastructure.Messaging;

public sealed class MessagingWorker(OutboxRelay relay, RabbitConsumer consumer, IOptions<RabbitOptions> options,
    ILogger<MessagingWorker> logger, WorkerProgress progress, IRetryBackoff? backoff = null) : BackgroundService
{
    private readonly IRetryBackoff retries = backoff ?? new JitteredRetryBackoff();
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            foreach (var name in new[] { WorkerNames.Outbox, WorkerNames.Payments, WorkerNames.Dispatch, WorkerNames.Realtime, WorkerNames.Financial }) progress.Disable(name);
            return Task.CompletedTask;
        }
        return Task.WhenAll(PublishLoopAsync(stoppingToken), ConsumeLoopAsync(consumer.RunPaymentsAsync, WorkerNames.Payments, stoppingToken),
            ConsumeLoopAsync(consumer.RunDispatchAsync, WorkerNames.Dispatch, stoppingToken),
            ConsumeLoopAsync(consumer.RunRealtimeAsync, WorkerNames.Realtime, stoppingToken),
            ConsumeLoopAsync(consumer.RunFinancialAsync, WorkerNames.Financial, stoppingToken));
    }

    private async Task PublishLoopAsync(CancellationToken ct)
    {
        progress.Begin(WorkerNames.Outbox);
        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    progress.BeginWork(WorkerNames.Outbox);
                    var count = await relay.RunBatchAsync(ct);
                    progress.Success(WorkerNames.Outbox);
                    failures = 0;
                    if (count == 0) await Task.Delay(options.Value.PollMilliseconds, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    progress.Failure(WorkerNames.Outbox);
                    logger.LogWarning("Outbox worker unavailable {FailureType}", error.GetType().Name);
                    failures = Math.Min(failures + 1, 30);
                    await Task.Delay(retries.GetDelay(failures, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)), ct);
                }
            }
        }
        finally { progress.Stop(WorkerNames.Outbox); }
    }

    private async Task ConsumeLoopAsync(Func<CancellationToken, Task> run, string consumerName, CancellationToken ct)
    {
        progress.Begin(consumerName);
        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await run(ct);
                    if (ct.IsCancellationRequested) break;
                    progress.Failure(consumerName);
                    logger.LogWarning("{Consumer} consumer unavailable {FailureType}", consumerName, FailureCodes.ConsumerTerminated);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception error) { progress.Failure(consumerName); logger.LogWarning("{Consumer} consumer unavailable {FailureType}", consumerName, error.GetType().Name); }
                failures = Math.Min(failures + 1, 30);
                await Task.Delay(retries.GetDelay(failures, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)), ct);
            }
        }
        finally { progress.Stop(consumerName); }
    }
}
