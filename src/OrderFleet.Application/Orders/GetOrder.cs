using OrderFleet.Application.Identity;
using OrderFleet.Domain.Orders;

namespace OrderFleet.Application.Orders;

public interface IOrderReadStore
{
    Task<Order?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> HasRestaurantMembershipAsync(Guid userId, Guid restaurantId, CancellationToken cancellationToken);
}

public sealed record OrderItemDetails(Guid ProductId, string Name, decimal UnitPrice, int Quantity, decimal TotalPrice);
public sealed record OrderDetails(Guid Id, Guid RestaurantId, string Status, string Currency, decimal TotalAmount,
    Guid? CourierId, Guid? AssignmentId, string? CancelReason, IReadOnlyList<OrderItemDetails> Items);

public sealed class GetOrder(IOrderReadStore store)
{
    public async Task<OrderDetails?> ExecuteAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        var order = await store.FindAsync(id, cancellationToken);
        if (order is null || !await CanReadAsync(order, actor, cancellationToken)) return null;
        return new(order.Id, order.RestaurantId, order.Status.ToString(), order.Currency, order.TotalAmount,
            order.CourierId, order.AssignmentId, order.CancelReason?.ToString(), order.Items
                .Select(x => new OrderItemDetails(x.ProductId, x.Name, x.UnitPrice, x.Quantity, x.TotalPrice)).ToArray());
    }

    private async Task<bool> CanReadAsync(Order order, Actor actor, CancellationToken cancellationToken)
    {
        if (actor.IsInRole(OrderFleetRoles.Admin)) return true;
        if (actor.IsInRole(OrderFleetRoles.Customer) && order.CustomerId == actor.Id) return true;
        if (actor.IsInRole(OrderFleetRoles.Courier) && order.CourierId == actor.Id) return true;
        return (actor.IsInRole(OrderFleetRoles.RestaurantStaff) || actor.IsInRole(OrderFleetRoles.RestaurantOwner))
            && await store.HasRestaurantMembershipAsync(actor.Id, order.RestaurantId, cancellationToken);
    }
}
