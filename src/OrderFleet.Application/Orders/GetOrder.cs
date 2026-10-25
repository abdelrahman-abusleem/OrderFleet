using OrderFleet.Application.Identity;
using OrderFleet.Domain.Orders;
using OrderFleet.Application.Catalog;

namespace OrderFleet.Application.Orders;

public interface IOrderReadStore
{
    Task<Order?> FindAsync(Guid id, CancellationToken cancellationToken);
}

public sealed record OrderItemDetails(Guid ProductId, string Name, decimal UnitPrice, int Quantity, decimal TotalPrice);
public sealed record OrderDetails(Guid Id, Guid RestaurantId, string Status, string Currency, decimal TotalAmount,
    Guid? CourierId, Guid? AssignmentId, string? CancelReason, IReadOnlyList<OrderItemDetails> Items);

public sealed class GetOrder(IOrderReadStore store, OrderAccessPolicy access)
{
    public async Task<OrderDetails?> ExecuteAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        var order = await store.FindAsync(id, cancellationToken);
        if (order is null || !await access.CanReadAsync(order.CustomerId, order.RestaurantId, order.CourierId, actor, cancellationToken)) return null;
        return Describe(order);
    }

    internal static OrderDetails Describe(Order order) => new(order.Id, order.RestaurantId, order.Status.ToString(), order.Currency, order.TotalAmount,
            order.CourierId, order.AssignmentId, order.CancelReason?.ToString(), order.Items
                .Select(x => new OrderItemDetails(x.ProductId, x.Name, x.UnitPrice, x.Quantity, x.TotalPrice)).ToArray());

}
