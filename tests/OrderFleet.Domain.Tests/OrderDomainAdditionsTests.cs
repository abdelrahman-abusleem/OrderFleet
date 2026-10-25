using System.Collections;
using OrderFleet.Domain.Orders;
using static OrderFleet.Domain.Tests.OrderTestSupport;

namespace OrderFleet.Domain.Tests;

public class OrderDomainAdditionsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 12, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Create_UsesSuppliedTime_AndRaisesOnlyCreatedEvent()
    {
        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), [Item()], Limits(), Start);
        Assert.Equal(Start, order.CreatedAt);
        Assert.Null(order.AcceptedAt);
        Assert.Null(order.AssignedAt);
        Assert.Null(order.PickedUpAt);
        Assert.Null(order.DeliveredAt);
        Assert.Null(order.CancelledAt);
        var created = Assert.IsType<OrderCreated>(Assert.Single(order.DomainEvents));
        AssertEvent(order, created, Start, OrderStatus.Created);
    }

    [Fact]
    public void DeliveryLifecycle_RecordsEachSuppliedTimestampAndOrderedEvent()
    {
        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), [Item()], Limits(), Start);
        var courier = Guid.NewGuid();
        order.Accept(Start.AddMinutes(1));
        var assignment = order.Assign(courier, Start.AddMinutes(2));
        order.MarkPickedUp(courier, assignment, Start.AddMinutes(3));
        order.Deliver(courier, assignment, Start.AddMinutes(4));

        Assert.Equal(Start, order.CreatedAt);
        Assert.Equal(Start.AddMinutes(1), order.AcceptedAt);
        Assert.Equal(Start.AddMinutes(2), order.AssignedAt);
        Assert.Equal(Start.AddMinutes(3), order.PickedUpAt);
        Assert.Equal(Start.AddMinutes(4), order.DeliveredAt);
        Assert.Null(order.CancelledAt);
        Assert.Collection(order.DomainEvents,
            e => Assert.IsType<OrderCreated>(e),
            e => Assert.IsType<OrderAccepted>(e),
            e => Assert.IsType<CourierAssigned>(e),
            e => Assert.IsType<OrderPickedUp>(e),
            e => Assert.IsType<OrderDelivered>(e));
        var states = new[] { OrderStatus.Created, OrderStatus.Accepted, OrderStatus.Assigned,
            OrderStatus.PickedUp, OrderStatus.Delivered };
        for (var i = 0; i < states.Length; i++)
            AssertEvent(order, order.DomainEvents[i], Start.AddMinutes(i), states[i]);
        Assert.Equal(courier, Assert.IsType<CourierAssigned>(order.DomainEvents[2]).CourierId);
        Assert.Equal(assignment, Assert.IsType<CourierAssigned>(order.DomainEvents[2]).AssignmentId);
        Assert.Equal(courier, Assert.IsType<OrderPickedUp>(order.DomainEvents[3]).CourierId);
        Assert.Equal(assignment, Assert.IsType<OrderPickedUp>(order.DomainEvents[3]).AssignmentId);
        Assert.Equal(courier, Assert.IsType<OrderDelivered>(order.DomainEvents[4]).CourierId);
        Assert.Equal(assignment, Assert.IsType<OrderDelivered>(order.DomainEvents[4]).AssignmentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleaseAndReassign_RecordsPreviousFenceAndFreshAssignmentTime(bool sameCourier)
    {
        var order = Create();
        order.Accept(Start);
        var courier = Guid.NewGuid();
        var assignment = order.Assign(courier, Start.AddMinutes(1));
        order.ClearDomainEvents();
        order.ReleaseAssignment(assignment, Start.AddMinutes(2));

        var released = Assert.IsType<AssignmentReleased>(Assert.Single(order.DomainEvents));
        AssertEvent(order, released, Start.AddMinutes(2), OrderStatus.Accepted);
        Assert.Equal(courier, released.PreviousCourierId);
        Assert.Equal(assignment, released.PreviousAssignmentId);
        Assert.Null(order.CourierId);
        Assert.Null(order.AssignmentId);
        Assert.Equal(Start, order.AcceptedAt);
        Assert.Equal(Start.AddMinutes(1), order.AssignedAt);

        var nextCourier = sameCourier ? courier : Guid.NewGuid();
        var nextAssignment = order.Assign(nextCourier, Start.AddMinutes(3));
        Assert.NotEqual(assignment, nextAssignment);
        Assert.Equal(Start.AddMinutes(3), order.AssignedAt);
        Assert.Equal(courier, released.PreviousCourierId);
        Assert.Equal(assignment, released.PreviousAssignmentId);
    }

    [Theory]
    [InlineData(OrderStatus.Created, CancellationReason.CustomerRequested)]
    [InlineData(OrderStatus.Created, CancellationReason.RestaurantRejected)]
    [InlineData(OrderStatus.Created, CancellationReason.AcceptanceTimedOut)]
    [InlineData(OrderStatus.Created, CancellationReason.AdminCancelled)]
    [InlineData(OrderStatus.Accepted, CancellationReason.RestaurantCancelled)]
    [InlineData(OrderStatus.Accepted, CancellationReason.AdminCancelled)]
    [InlineData(OrderStatus.Assigned, CancellationReason.AdminCancelled)]
    public void Cancel_EveryAllowedPath_RecordsTimeReasonAndPreviousAssignment(
        OrderStatus state, CancellationReason reason)
    {
        var order = InState(state);
        var courier = order.CourierId;
        var assignment = order.AssignmentId;
        var createdAt = order.CreatedAt;
        var acceptedAt = order.AcceptedAt;
        var assignedAt = order.AssignedAt;
        order.ClearDomainEvents();
        order.Cancel(reason, Start);

        var cancelled = Assert.IsType<OrderCancelled>(Assert.Single(order.DomainEvents));
        AssertEvent(order, cancelled, Start, OrderStatus.Cancelled);
        Assert.Equal(reason, cancelled.Reason);
        Assert.Equal(courier, cancelled.PreviousCourierId);
        Assert.Equal(assignment, cancelled.PreviousAssignmentId);
        Assert.Equal(Start, order.CancelledAt);
        Assert.Equal(createdAt, order.CreatedAt);
        Assert.Equal(acceptedAt, order.AcceptedAt);
        Assert.Equal(assignedAt, order.AssignedAt);
    }

    [Fact]
    public void EventCollection_IsReadOnly_AndClearDoesNotChangeBusinessState()
    {
        var order = Create();
        var events = order.DomainEvents;
        var created = Assert.Single(events);
        var list = Assert.IsAssignableFrom<IList>(events);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Clear());
        var snapshot = new Snapshot(order, includeLifecycle: false);

        order.ClearDomainEvents();
        Assert.Empty(order.DomainEvents);
        snapshot.AssertUnchanged(order);
        order.ClearDomainEvents();
        order.Accept(Start);
        Assert.IsType<OrderAccepted>(Assert.Single(order.DomainEvents));
        Assert.Equal(OrderStatus.Created, created.Status);
    }

    private static void AssertEvent(Order order, OrderDomainEvent domainEvent,
        DateTimeOffset occurredAt, OrderStatus status)
    {
        Assert.NotEqual(Guid.Empty, domainEvent.EventId);
        Assert.Equal(order.Id, domainEvent.OrderId);
        Assert.Equal(order.TotalAmount, domainEvent.TotalAmount);
        Assert.Equal(occurredAt, domainEvent.OccurredAt);
        Assert.Equal(status, domainEvent.Status);
    }
}
