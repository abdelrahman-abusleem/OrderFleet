using OrderFleet.Domain.Orders;

namespace OrderFleet.Domain.Tests;

internal static class OrderTestSupport
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    // "Normal business" limits. Every order produced by the helpers below
    // fits comfortably inside them, so individual tests can opt into exotic
    // limits explicitly without disturbing the shared ones.
    internal static OrderPricingLimits Limits() => new(
        maxQuantityPerItem: 99,
        maxItemsPerOrder: 20,
        maxUnitPrice: 9_999.99m,
        maxOrderTotal: 99_999.99m);

    // Admits the entire decimal range for a single line.
    internal static OrderPricingLimits SingleItemLimits() => new(
        maxQuantityPerItem: 1,
        maxItemsPerOrder: 1,
        maxUnitPrice: decimal.MaxValue,
        maxOrderTotal: decimal.MaxValue);

    internal static OrderItem Item(decimal price = 12.50m, int quantity = 2) =>
        new(Guid.NewGuid(), "Shawarma", price, quantity);

    internal static OrderItem Item(Guid productId, decimal price = 12.50m, int quantity = 2) =>
        new(productId, "Shawarma", price, quantity);

    internal static Order Create() =>
        Order.Create(Guid.NewGuid(), Guid.NewGuid(), [Item(), Item(3m, 1)], Limits(), Now);

    // Reach each state exclusively through the public domain API.
    internal static Order InState(OrderStatus status)
    {
        var order = Create();
        if (status == OrderStatus.Created) return order;
        if (status == OrderStatus.Cancelled)
        {
            order.Cancel(CancellationReason.CustomerRequested, Now);
            return order;
        }

        order.Accept(Now);
        if (status == OrderStatus.Accepted) return order;
        var courier = Guid.NewGuid();
        var assignment = order.Assign(courier, Now);
        if (status == OrderStatus.Assigned) return order;
        order.MarkPickedUp(courier, assignment, Now);
        if (status == OrderStatus.PickedUp) return order;
        order.Deliver(courier, assignment, Now);
        return order;
    }

    internal sealed class Snapshot(Order order, bool includeLifecycle = true)
    {
        private readonly Guid id = order.Id;
        private readonly Guid customerId = order.CustomerId;
        private readonly Guid restaurantId = order.RestaurantId;
        private readonly OrderStatus status = order.Status;
        private readonly Guid? courierId = order.CourierId;
        private readonly Guid? assignmentId = order.AssignmentId;
        private readonly CancellationReason? cancelReason = order.CancelReason;
        private readonly string currency = order.Currency;
        private readonly decimal total = order.TotalAmount;
        private readonly OrderItem[] items = order.Items.ToArray();
        private readonly OrderDomainEvent[] events = order.DomainEvents.ToArray();
        private readonly (DateTimeOffset, DateTimeOffset?, DateTimeOffset?, DateTimeOffset?, DateTimeOffset?, DateTimeOffset?)
            timestamps = (order.CreatedAt, order.AcceptedAt, order.AssignedAt, order.PickedUpAt,
                order.DeliveredAt, order.CancelledAt);
        private readonly (Guid, string, decimal, int, decimal)[] itemValues = order.Items
            .Select(x => (x.ProductId, x.Name, x.UnitPrice, x.Quantity, x.TotalPrice)).ToArray();

        internal void AssertUnchanged(Order actual)
        {
            if (includeLifecycle)
            {
                Assert.Equal(events, actual.DomainEvents);
                Assert.Equal(timestamps, (actual.CreatedAt, actual.AcceptedAt, actual.AssignedAt,
                    actual.PickedUpAt, actual.DeliveredAt, actual.CancelledAt));
            }
            Assert.Equal(id, actual.Id);
            Assert.Equal(customerId, actual.CustomerId);
            Assert.Equal(restaurantId, actual.RestaurantId);
            Assert.Equal(status, actual.Status);
            Assert.Equal(courierId, actual.CourierId);
            Assert.Equal(assignmentId, actual.AssignmentId);
            Assert.Equal(cancelReason, actual.CancelReason);
            Assert.Equal(currency, actual.Currency);
            Assert.Equal(total, actual.TotalAmount);
            Assert.Equal(items.Length, actual.Items.Count);
            for (var i = 0; i < items.Length; i++)
            {
                Assert.Same(items[i], actual.Items[i]);
                var item = actual.Items[i];
                Assert.Equal(itemValues[i],
                    (item.ProductId, item.Name, item.UnitPrice, item.Quantity, item.TotalPrice));
            }
        }
    }
}
