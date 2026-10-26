using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFleet.Application.Clock;
using OrderFleet.Application.Messaging;
using OrderFleet.Application.Diagnostics;

namespace OrderFleet.Infrastructure.Messaging;

public sealed class OutboxRelay(IOutboxStore store, IEventPublisher publisher, IClock clock,
    IOptions<RabbitOptions> options, ILogger<OutboxRelay> logger)
{
    public async Task<int> RunBatchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var leases = await store.ClaimAsync(5, clock.UtcNow, TimeSpan.FromSeconds(settings.LeaseSeconds), cancellationToken);
        foreach (var lease in leases)
        {
            try
            {
                await publisher.PublishAsync(lease.Message, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                var code = error is InvalidDataException or System.Text.Json.JsonException ? "InvalidEnvelope" : "BrokerUnavailable";
                OrderFleetDiagnostics.OutboxRetries.Add(1, new KeyValuePair<string, object?>("reason", code));
                await store.FailAsync(lease, clock.UtcNow, code, code == "InvalidEnvelope" ? 1 : settings.OutboxMaxAttempts, cancellationToken);
                logger.LogWarning("Outbox publication failed {MessageId} {FailureCode} attempt {Attempt}", lease.Message.MessageId, code, lease.Attempts);
                continue;
            }
            OrderFleetDiagnostics.OutboxPublished.Add(1);
            OrderFleetDiagnostics.OutboxLag.Record(Math.Max(0, (clock.UtcNow - lease.Message.OccurredAtUtc).TotalSeconds));
            try
            {
                if (!await store.CompleteAsync(lease, clock.UtcNow, cancellationToken))
                    logger.LogWarning("Outbox lease expired for {MessageId}", lease.Message.MessageId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // Publication is confirmed. Leave this lease unchanged for expiry;
                // a later delivery may duplicate the fact, so consumers use Inbox.
                logger.LogWarning("Outbox completion deferred {MessageId} {FailureType}", lease.Message.MessageId, error.GetType().Name);
            }
        }
        return leases.Count;
    }
}
