namespace OrderFleet.Infrastructure.Orders;

public sealed class OrderAuditEntry
{
    public Guid Id { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid OrderId { get; private set; }
    public string Action { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }

    private OrderAuditEntry() { }
    public OrderAuditEntry(Guid actorId, Guid orderId, string action, DateTimeOffset occurredAt)
    {
        Id = Guid.NewGuid();
        ActorId = actorId;
        OrderId = orderId;
        Action = action;
        OccurredAt = occurredAt.ToUniversalTime();
    }
}
