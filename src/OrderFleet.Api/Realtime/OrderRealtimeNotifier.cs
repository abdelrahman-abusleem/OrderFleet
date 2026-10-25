using Microsoft.AspNetCore.SignalR;
using OrderFleet.Application.Clock;
using OrderFleet.Application.Couriers;
using OrderFleet.Application.Realtime;

namespace OrderFleet.Api.Realtime;

public sealed class OrderRealtimeNotifier(OrderConnections connections, IHubContext<OrdersHub> hub,
    IServiceScopeFactory scopes, IClock clock, CourierPolicy policy) : IOrderNotifier
{
    public async Task<OrderSubscriptionSnapshot> SubscribeAsync(string connectionId, Guid orderId, CancellationToken ct)
    {
        var connection = connections.Find(connectionId) ?? throw new HubException("Connection unavailable.");
        await connection.Gate.WaitAsync(ct);
        try
        {
            if (!Valid(connection)) throw new HubException("Connection expired.");
            using var scope = scopes.CreateScope();
            var current = await scope.ServiceProvider.GetRequiredService<GetRealtimeOrder>().ExecuteAsync(orderId, connection.Actor, ct)
                ?? throw new HubException("Order unavailable.");
            if (!connection.Subscriptions.ContainsKey(orderId) && connection.Subscriptions.Count >= 20)
                throw new HubException("Subscription limit reached.");
            var group = $"private:order:{orderId:N}:connection:{connectionId}";
            await hub.Groups.AddToGroupAsync(connectionId, group, ct);
            var location = Fresh(current.Location) ? current.Location : null;
            connection.Subscriptions[orderId] = new(group, current.Update.Sequence)
            {
                LastLocationObservedAt = location?.ObservedAt,
                LastLocationSentAt = location is null ? null : clock.UtcNow
            };
            return new(current.Update, location);
        }
        finally { connection.Gate.Release(); }
    }

    public async Task UnsubscribeAsync(string connectionId, Guid orderId, CancellationToken ct)
    {
        var connection = connections.Find(connectionId);
        if (connection is null) return;
        await connection.Gate.WaitAsync(ct);
        try
        {
            if (connection.Subscriptions.Remove(orderId, out var subscription))
                await hub.Groups.RemoveFromGroupAsync(connectionId, subscription.Group, ct);
        }
        finally { connection.Gate.Release(); }
    }

    public async Task NotifyAsync(Guid orderId, bool locationOnly, CancellationToken ct)
    {
        foreach (var connection in connections.Snapshot())
        {
            await connection.Gate.WaitAsync(ct);
            try
            {
                if (!connection.Subscriptions.TryGetValue(orderId, out var subscription)) continue;
                using var scope = scopes.CreateScope();
                var current = Valid(connection)
                    ? await scope.ServiceProvider.GetRequiredService<GetRealtimeOrder>().ExecuteAsync(orderId, connection.Actor, ct) : null;
                if (current is null)
                {
                    connection.Subscriptions.Remove(orderId);
                    await hub.Groups.RemoveFromGroupAsync(connection.Id, subscription.Group, ct);
                    continue;
                }
                if (!locationOnly && current.Update.Sequence > subscription.LastSequence)
                {
                    await hub.Clients.Group(subscription.Group).SendAsync("OrderUpdated", current.Update, ct);
                    subscription.LastSequence = current.Update.Sequence;
                }
                var location = current.Location;
                if (Fresh(location) && (subscription.LastLocationObservedAt is null || location!.ObservedAt > subscription.LastLocationObservedAt)
                    && (subscription.LastLocationSentAt is null || clock.UtcNow - subscription.LastLocationSentAt >= TimeSpan.FromSeconds(2)))
                {
                    await hub.Clients.Group(subscription.Group).SendAsync("CourierLocationUpdated", location, ct);
                    subscription.LastLocationObservedAt = location!.ObservedAt;
                    subscription.LastLocationSentAt = clock.UtcNow;
                }
            }
            finally { connection.Gate.Release(); }
        }
    }

    private bool Valid(OrderConnection connection) => connection.Active && connection.ExpiresAt > clock.UtcNow;
    private bool Fresh(CourierLocationUpdate? location) => location is not null
        && location.ObservedAt >= clock.UtcNow - policy.LocationFreshness && location.ObservedAt <= clock.UtcNow + policy.FutureSkew;
}
