using OrderFleet.Domain.Geo;
using OrderFleet.Domain.Orders;

namespace OrderFleet.Domain.Couriers;

public enum CourierStatus { Offline = 1, Available, Reserved, Busy }

public sealed class Courier
{
    public Guid Id { get; private set; }
    public CourierStatus Status { get; private set; }
    public decimal? Latitude { get; private set; }
    public decimal? Longitude { get; private set; }
    public DateTimeOffset? LocationObservedAt { get; private set; }
    public Guid? OrderId { get; private set; }
    public Guid? AssignmentId { get; private set; }

    private Courier() { }
    public static Courier Register(Guid identity)
    {
        if (identity == Guid.Empty) throw new ArgumentException("Courier identity required.", nameof(identity));
        return new() { Id = identity, Status = CourierStatus.Offline };
    }

    public void SetAvailable(bool available)
    {
        if (Status is CourierStatus.Reserved or CourierStatus.Busy) throw new DomainException("Courier has an active assignment.");
        Status = available ? CourierStatus.Available : CourierStatus.Offline;
    }

    public void UpdateLocation(Coordinates coordinates, DateTimeOffset observedAt)
    {
        if (LocationObservedAt.HasValue && observedAt <= LocationObservedAt.Value)
            throw new DomainException("Location must be newer than the current observation.");
        Latitude = coordinates.Latitude;
        Longitude = coordinates.Longitude;
        LocationObservedAt = observedAt;
    }

    public void Reserve(Guid orderId, Guid assignmentId)
    {
        if (orderId == Guid.Empty || assignmentId == Guid.Empty) throw new ArgumentException("Assignment references required.");
        if (Status != CourierStatus.Available) throw new DomainException("Courier is unavailable.");
        OrderId = orderId;
        AssignmentId = assignmentId;
        Status = CourierStatus.Reserved;
    }

    public void MarkBusy(Guid orderId, Guid assignmentId)
    {
        RequireAssignment(orderId, assignmentId);
        if (Status != CourierStatus.Reserved) throw new DomainException("Courier is not reserved.");
        Status = CourierStatus.Busy;
    }

    public void Release(Guid orderId, Guid assignmentId)
    {
        RequireAssignment(orderId, assignmentId);
        OrderId = null;
        AssignmentId = null;
        Status = CourierStatus.Available;
    }

    private void RequireAssignment(Guid orderId, Guid assignmentId)
    {
        if (orderId == Guid.Empty || assignmentId == Guid.Empty) throw new ArgumentException("Assignment references required.");
        if (Status is not (CourierStatus.Reserved or CourierStatus.Busy) || OrderId != orderId || AssignmentId != assignmentId)
            throw new DomainException("Courier assignment is no longer current.");
    }
}
