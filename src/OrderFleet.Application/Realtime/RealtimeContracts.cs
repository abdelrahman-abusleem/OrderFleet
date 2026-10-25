using OrderFleet.Application.Identity;

namespace OrderFleet.Application.Realtime;

public sealed record OrderUpdate(Guid OrderId, long Sequence, string Status, Guid? CourierId, Guid? AssignmentId);
public sealed record CourierLocationUpdate(Guid OrderId, Guid CourierId, Guid AssignmentId, decimal Latitude,
    decimal Longitude, DateTimeOffset ObservedAt);
public sealed record RealtimeOrderSnapshot(Guid CustomerId, Guid RestaurantId, OrderUpdate Update, CourierLocationUpdate? Location);
public sealed record OrderSubscriptionSnapshot(OrderUpdate Order, CourierLocationUpdate? CourierLocation);

public interface IRealtimeStore
{
    Task<Actor> RefreshActorAsync(Actor tokenActor, CancellationToken ct);
    Task<RealtimeOrderSnapshot?> ReadAsync(Guid orderId, CancellationToken ct);
    Task<Guid?> ActiveOrderAsync(Guid courierId, CancellationToken ct);
    Task<bool> IsProcessedAsync(Guid messageId, CancellationToken ct);
    Task MarkProcessedAsync(Guid messageId, DateTimeOffset now, CancellationToken ct);
}

public interface IOrderNotifier
{
    Task NotifyAsync(Guid orderId, bool locationOnly, CancellationToken ct);
}
