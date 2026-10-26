using OrderFleet.Domain.Orders;
using OrderFleet.Domain.Common;
using static OrderFleet.Domain.Tests.OrderTestSupport;

namespace OrderFleet.Domain.Tests;

public class OrderCancellationTests
{
    // Explicit specification rows keep expectations independent of the production switch.
    [Theory]
    [InlineData(OrderStatus.Created, CancellationReason.CustomerRequested)]
    [InlineData(OrderStatus.Created, CancellationReason.RestaurantRejected)]
    [InlineData(OrderStatus.Created, CancellationReason.AcceptanceTimedOut)]
    [InlineData(OrderStatus.Created, CancellationReason.AdminCancelled)]
    [InlineData(OrderStatus.Accepted, CancellationReason.RestaurantCancelled)]
    [InlineData(OrderStatus.Accepted, CancellationReason.AdminCancelled)]
    [InlineData(OrderStatus.Assigned, CancellationReason.AdminCancelled)]
    public void Cancel_AllAllowedCombinations_SetReasonAndClearAssignment(
        OrderStatus status, CancellationReason reason)
    {
        var order = InState(status);
        var id = order.Id;
        var customer = order.CustomerId;
        var restaurant = order.RestaurantId;
        var items = order.Items.ToArray();
        var total = order.TotalAmount;
        if (status == OrderStatus.Assigned)
        {
            Assert.NotNull(order.CourierId);
            Assert.NotNull(order.AssignmentId);
        }

        order.Cancel(reason, Now);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(reason, order.CancelReason);
        Assert.Null(order.CourierId);
        Assert.Null(order.AssignmentId);
        Assert.Equal(id, order.Id);
        Assert.Equal(customer, order.CustomerId);
        Assert.Equal(restaurant, order.RestaurantId);
        Assert.Equal(items, order.Items);
        Assert.Equal(total, order.TotalAmount);
        Assert.Equal("SAR", order.Currency);

        var cancelled = new Snapshot(order);
        Assert.Throws<DomainException>(() => order.Accept(Now));
        cancelled.AssertUnchanged(order);
        Assert.Throws<DomainException>(() => order.Assign(Guid.NewGuid(), Now));
        cancelled.AssertUnchanged(order);
        Assert.Throws<DomainException>(() => order.MarkPickedUp(Guid.NewGuid(), Guid.NewGuid(), Now));
        cancelled.AssertUnchanged(order);
        Assert.Throws<DomainException>(() => order.Deliver(Guid.NewGuid(), Guid.NewGuid(), Now));
        cancelled.AssertUnchanged(order);
        Assert.Throws<DomainException>(() => order.ReleaseAssignment(Guid.NewGuid(), Now));
        cancelled.AssertUnchanged(order);
        foreach (var nextReason in Enum.GetValues<CancellationReason>())
        {
            Assert.Throws<DomainException>(() => order.Cancel(nextReason, Now));
            cancelled.AssertUnchanged(order);
        }
    }

    public static IEnumerable<object[]> InvalidCancellations()
    {
        yield return [OrderStatus.Created, CancellationReason.RestaurantCancelled];
        yield return [OrderStatus.Accepted, CancellationReason.CustomerRequested];
        yield return [OrderStatus.Accepted, CancellationReason.RestaurantRejected];
        yield return [OrderStatus.Accepted, CancellationReason.AcceptanceTimedOut];
        yield return [OrderStatus.Assigned, CancellationReason.CustomerRequested];
        yield return [OrderStatus.Assigned, CancellationReason.RestaurantRejected];
        yield return [OrderStatus.Assigned, CancellationReason.AcceptanceTimedOut];
        yield return [OrderStatus.Assigned, CancellationReason.RestaurantCancelled];
        foreach (var status in new[] { OrderStatus.PickedUp, OrderStatus.Delivered, OrderStatus.Cancelled })
            foreach (var reason in Enum.GetValues<CancellationReason>())
                yield return [status, reason];

        foreach (var status in Enum.GetValues<OrderStatus>())
            foreach (var reason in new[] { 0, -1, 6, int.MinValue, int.MaxValue })
                yield return [status, (CancellationReason)reason];
    }

    [Theory]
    [MemberData(nameof(InvalidCancellations))]
    public void Cancel_EveryDisallowedOrUndefinedReason_IsRejectedWithoutMutation(
        OrderStatus status, CancellationReason reason)
    {
        var order = InState(status);
        var snapshot = new Snapshot(order);
        Assert.Throws<DomainException>(() => order.Cancel(reason, Now));
        snapshot.AssertUnchanged(order);
    }
}
