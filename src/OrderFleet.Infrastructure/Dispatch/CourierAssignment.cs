namespace OrderFleet.Infrastructure.Dispatch;

public sealed class CourierAssignment
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid CourierId { get; private set; }
    public DateTimeOffset AssignedAtUtc { get; private set; }
    public DateTimeOffset PickupDeadlineUtc { get; private set; }
    public DateTimeOffset? PickedUpAtUtc { get; private set; }
    public DateTimeOffset? ReleasedAtUtc { get; private set; }
    public DateTimeOffset? DeliveredAtUtc { get; private set; }
    private CourierAssignment() { }
    internal CourierAssignment(Guid id, Guid orderId, Guid courierId, DateTimeOffset now, TimeSpan pickupTimeout)
    { Id = id; OrderId = orderId; CourierId = courierId; AssignedAtUtc = now; PickupDeadlineUtc = now + pickupTimeout; }
    internal void RecordPickup(DateTimeOffset now) => PickedUpAtUtc ??= now;
    internal void RecordRelease(DateTimeOffset now) => ReleasedAtUtc ??= now;
    internal void RecordDelivery(DateTimeOffset now) => DeliveredAtUtc ??= now;
}
