using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFleet.Application.Clock;
using OrderFleet.Application.Messaging;

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
                if (!await store.CompleteAsync(lease, clock.UtcNow, cancellationToken))
                    logger.LogWarning("Outbox lease expired for {MessageId}", lease.Message.MessageId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                var code = error is InvalidDataException or System.Text.Json.JsonException ? "InvalidEnvelope" : "BrokerUnavailable";
                await store.FailAsync(lease, clock.UtcNow, code, code == "InvalidEnvelope" ? 1 : settings.OutboxMaxAttempts, cancellationToken);
                logger.LogWarning("Outbox publication failed {MessageId} {FailureCode} attempt {Attempt}", lease.Message.MessageId, code, lease.Attempts);
            }
        }
        return leases.Count;
    }
}
