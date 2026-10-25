using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using OrderFleet.Api.Identity;
using OrderFleet.Application.Clock;
using OrderFleet.Application.Realtime;

namespace OrderFleet.Api.Realtime;

[Authorize]
public sealed class OrdersHub(OrderConnections connections, OrderRealtimeNotifier notifier, IClock clock) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (!long.TryParse(Context.User?.FindFirst("exp")?.Value, out var expiration)
            || DateTimeOffset.FromUnixTimeSeconds(expiration) <= clock.UtcNow
            || !connections.Add(Context.ConnectionId, Context.User!.ToActor(), DateTimeOffset.FromUnixTimeSeconds(expiration)))
        {
            Context.Abort();
            throw new HubException("Connection unavailable.");
        }
        await base.OnConnectedAsync();
    }

    public Task<OrderSubscriptionSnapshot> SubscribeOrder(Guid orderId) =>
        notifier.SubscribeAsync(Context.ConnectionId, orderId, Context.ConnectionAborted);

    public Task UnsubscribeOrder(Guid orderId) => notifier.UnsubscribeAsync(Context.ConnectionId, orderId, Context.ConnectionAborted);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
