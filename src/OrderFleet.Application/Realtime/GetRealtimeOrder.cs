using OrderFleet.Application.Identity;
using OrderFleet.Application.Orders;

namespace OrderFleet.Application.Realtime;

public sealed class GetRealtimeOrder(IRealtimeStore store, OrderAccessPolicy access)
{
    public async Task<RealtimeOrderSnapshot?> ExecuteAsync(Guid orderId, Actor tokenActor, CancellationToken ct)
    {
        var actor = await store.RefreshActorAsync(tokenActor, ct);
        var snapshot = await store.ReadAsync(orderId, ct);
        if (snapshot is null || !await access.CanReadAsync(snapshot.CustomerId, snapshot.RestaurantId,
                snapshot.Update.CourierId, actor, ct)) return null;
        return snapshot;
    }
}
