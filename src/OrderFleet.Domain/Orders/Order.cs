
using OrderFleet.Domain.Common;

namespace OrderFleet.Domain.Orders;

public sealed class Order
{
    private readonly List<OrderItem> _items = [];
    private readonly List<OrderDomainEvent> _domainEvents = [];

    public IReadOnlyList<OrderDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public DateTimeOffset? AssignedAt { get; private set; }
    public DateTimeOffset? PickedUpAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }

    public void ClearDomainEvents() => _domainEvents.Clear();

    public Guid Id { get; private set; }

    public Guid CustomerId { get; private set; }

    public Guid RestaurantId { get; private set; }

    public OrderStatus Status { get; private set; }

    public Guid? CourierId { get; private set; }

    public Guid? AssignmentId { get; private set; }

    public CancellationReason? CancelReason { get; private set; }

    public string Currency { get; private set; } = CurrencyCodes.Sar;

    public IReadOnlyList<OrderItem> Items =>
        _items.AsReadOnly();

    public decimal TotalAmount { get; private set; }

    private Order() { } // EF Core

    private Order(
        Guid id,
        Guid customerId,
        Guid restaurantId,
        IEnumerable<OrderItem> items,
        decimal totalAmount,
        DateTimeOffset now)
    {
        Id = id;
        CustomerId = customerId;
        RestaurantId = restaurantId;
        Status = OrderStatus.Created;
        TotalAmount = totalAmount;
        CreatedAt = now;

        _items.AddRange(items);
        _domainEvents.Add(new OrderCreated(Id, TotalAmount, now));
    }

    public static Order Create(
        Guid customerId,
        Guid restaurantId,
        IEnumerable<OrderItem> items,
        OrderPricingLimits limits,
        DateTimeOffset now)
    {
        if (customerId == Guid.Empty)
            throw new ArgumentException(
                "Customer ID cannot be empty.",
                nameof(customerId));

        if (restaurantId == Guid.Empty)
            throw new ArgumentException(
                "Restaurant ID cannot be empty.",
                nameof(restaurantId));

        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(limits);

        var itemList = items.ToList();

        if (itemList.Count == 0)
            throw new ArgumentException(
                "Order must contain at least one item.",
                nameof(items));

        if (itemList.Count > limits.MaxItemsPerOrder)
            throw new ArgumentException(
                $"Order cannot contain more than " +
                $"{limits.MaxItemsPerOrder} items.",
                nameof(items));

        if (itemList.Any(item => item is null))
            throw new ArgumentException(
                "Order items cannot contain null.",
                nameof(items));

        if (itemList.Select(item => item.ProductId)
                .Distinct().Count() != itemList.Count)
        {
            throw new ArgumentException(
                "Order cannot contain the same product more than once.",
                nameof(items));
        }

        decimal totalAmount = 0m;

        foreach (var item in itemList)
        {
            if (item.Quantity > limits.MaxQuantityPerItem)
            {
                throw new ArgumentException(
                    $"Item quantity cannot exceed " +
                    $"{limits.MaxQuantityPerItem}.",
                    nameof(items));
            }

            if (item.UnitPrice > limits.MaxUnitPrice)
            {
                throw new ArgumentException(
                    $"Unit price cannot exceed " +
                    $"{limits.MaxUnitPrice} SAR.",
                    nameof(items));
            }

            totalAmount += item.TotalPrice;
        }

        if (totalAmount > limits.MaxOrderTotal)
        {
            throw new ArgumentException(
                $"Order total cannot exceed " +
                $"{limits.MaxOrderTotal} SAR.",
                nameof(items));
        }

        return new Order(
            Guid.NewGuid(),
            customerId,
            restaurantId,
            itemList,
            totalAmount,
            now);
    }

    public void Accept(DateTimeOffset now)
    {
        if (Status != OrderStatus.Created)
        {
            throw new DomainException(
                $"Cannot accept an order in {Status} state.");
        }

        Status = OrderStatus.Accepted;
        AcceptedAt = now;
        _domainEvents.Add(new OrderAccepted(Id, TotalAmount, now));
    }

    public Guid Assign(Guid courierId, DateTimeOffset now)
    {
        if (courierId == Guid.Empty)
            throw new ArgumentException(
                "Courier ID cannot be empty.",
                nameof(courierId));

        if (Status != OrderStatus.Accepted)
        {
            throw new DomainException(
                $"Cannot assign a courier to an order in {Status} state.");
        }

        var assignmentId = Guid.NewGuid();

        CourierId = courierId;
        AssignmentId = assignmentId;
        Status = OrderStatus.Assigned;
        AssignedAt = now;
        _domainEvents.Add(new CourierAssigned(Id, TotalAmount, now, courierId, assignmentId));

        return assignmentId;
    }

    public void MarkPickedUp(
        Guid courierId,
        Guid assignmentId,
        DateTimeOffset now)
    {
        if (Status != OrderStatus.Assigned)
        {
            throw new DomainException(
                $"Cannot pick up an order in {Status} state.");
        }

        EnsureCurrentAssignment(courierId, assignmentId);

        Status = OrderStatus.PickedUp;
        PickedUpAt = now;
        _domainEvents.Add(new OrderPickedUp(Id, TotalAmount, now, courierId, assignmentId));
    }

    public void Deliver(
        Guid courierId,
        Guid assignmentId,
        DateTimeOffset now)
    {
        if (Status != OrderStatus.PickedUp)
        {
            throw new DomainException(
                $"Cannot deliver an order in {Status} state.");
        }

        EnsureCurrentAssignment(courierId, assignmentId);

        Status = OrderStatus.Delivered;
        DeliveredAt = now;
        _domainEvents.Add(new OrderDelivered(Id, TotalAmount, now, courierId, assignmentId));
    }

    public void ReleaseAssignment(Guid assignmentId, DateTimeOffset now)
    {
        if (Status != OrderStatus.Assigned)
        {
            throw new DomainException(
                $"Cannot release an assignment in {Status} state.");
        }

        if (assignmentId == Guid.Empty ||
            AssignmentId != assignmentId)
        {
            throw new DomainException(
                "The assignment is no longer current.");
        }

        var previousCourierId = CourierId!.Value;
        var previousAssignmentId = AssignmentId!.Value;
        CourierId = null;
        AssignmentId = null;
        Status = OrderStatus.Accepted;
        _domainEvents.Add(new AssignmentReleased(Id, TotalAmount, now,
            previousCourierId, previousAssignmentId));
    }

    public void Cancel(CancellationReason reason, DateTimeOffset now)
    {
        var allowed = Status switch
        {
            OrderStatus.Created =>
                reason is CancellationReason.CustomerRequested
                    or CancellationReason.RestaurantRejected
                    or CancellationReason.AcceptanceTimedOut
                    or CancellationReason.AdminCancelled,

            OrderStatus.Accepted =>
                reason is CancellationReason.RestaurantCancelled
                    or CancellationReason.AdminCancelled,

            OrderStatus.Assigned =>
                reason is CancellationReason.AdminCancelled,

            _ => false
        };

        if (!allowed)
        {
            throw new DomainException(
                $"Cannot cancel an order in {Status} state " +
                $"with reason {reason}.");
        }

        var previousCourierId = CourierId;
        var previousAssignmentId = AssignmentId;
        CancelReason = reason;
        CourierId = null;
        AssignmentId = null;
        Status = OrderStatus.Cancelled;
        CancelledAt = now;
        _domainEvents.Add(new OrderCancelled(Id, TotalAmount, now, reason,
            previousCourierId, previousAssignmentId));
    }

    private void EnsureCurrentAssignment(
        Guid courierId,
        Guid assignmentId)
    {
        if (courierId == Guid.Empty ||
            assignmentId == Guid.Empty ||
            CourierId != courierId ||
            AssignmentId != assignmentId)
        {
            throw new DomainException(
                "The courier assignment is not current.");
        }
    }
}
