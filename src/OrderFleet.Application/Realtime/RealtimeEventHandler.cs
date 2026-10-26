using OrderFleet.Application.Clock;
using OrderFleet.Application.Messaging;

namespace OrderFleet.Application.Realtime;

public sealed class RealtimeEventHandler(IRealtimeStore store, IOrderNotifier notifier, IClock clock) : IIntegrationEventHandler
{
    public async Task HandleAsync(IntegrationEvent message, CancellationToken ct)
    {
        if (message.Version != 1) throw new InvalidDataException("Unsupported realtime event version.");
        if (await store.IsProcessedAsync(message.MessageId, ct)) return;
        var locationOnly = message.Type == IntegrationEventTypes.CourierLocationUpdated;
        if (!locationOnly && !message.Type.StartsWith(IntegrationEventTypes.OrderPrefix, StringComparison.Ordinal)) throw new InvalidDataException("Unsupported realtime event.");
        var orderId = locationOnly ? await store.ActiveOrderAsync(message.AggregateId, ct) : message.AggregateId;
        if (orderId.HasValue) await notifier.NotifyAsync(orderId.Value, locationOnly, ct);
        await store.MarkProcessedAsync(message.MessageId, clock.UtcNow, ct);
    }
}
