namespace OrderFleet.Domain.Orders;

public abstract record OrderDomainEvent(Guid OrderId, OrderStatus Status,
    decimal TotalAmount, DateTimeOffset OccurredAt)
{
    public Guid EventId { get; } = Guid.NewGuid();
}

public sealed record OrderCreated(Guid OrderId, decimal TotalAmount, DateTimeOffset OccurredAt)
    : OrderDomainEvent(OrderId, OrderStatus.Created, TotalAmount, OccurredAt);

public sealed record OrderAccepted(Guid OrderId, decimal TotalAmount, DateTimeOffset OccurredAt)
    : OrderDomainEvent(OrderId, OrderStatus.Accepted, TotalAmount, OccurredAt);

public sealed record CourierAssigned(Guid OrderId, decimal TotalAmount, DateTimeOffset OccurredAt,
    Guid CourierId, Guid AssignmentId)
    : OrderDomainEvent(OrderId, OrderStatus.Assigned, TotalAmount, OccurredAt);

public sealed record AssignmentReleased(Guid OrderId, decimal TotalAmount, DateTimeOffset OccurredAt,
    Guid PreviousCourierId, Guid PreviousAssignmentId)
    : OrderDomainEvent(OrderId, OrderStatus.Accepted, TotalAmount, OccurredAt);

public sealed record OrderPickedUp(Guid OrderId, decimal TotalAmount, DateTimeOffset OccurredAt,
    Guid CourierId, Guid AssignmentId)
    : OrderDomainEvent(OrderId, OrderStatus.PickedUp, TotalAmount, OccurredAt);

public sealed record OrderDelivered(Guid OrderId, decimal TotalAmount, DateTimeOffset OccurredAt,
    Guid CourierId, Guid AssignmentId)
    : OrderDomainEvent(OrderId, OrderStatus.Delivered, TotalAmount, OccurredAt);

public sealed record OrderCancelled(Guid OrderId, decimal TotalAmount, DateTimeOffset OccurredAt,
    CancellationReason Reason, Guid? PreviousCourierId, Guid? PreviousAssignmentId)
    : OrderDomainEvent(OrderId, OrderStatus.Cancelled, TotalAmount, OccurredAt);
