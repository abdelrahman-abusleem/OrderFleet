using NetTopologySuite.Geometries;
using OrderFleet.Domain.Couriers;

namespace OrderFleet.Infrastructure.Couriers;

// Spatial read projection of Courier's location. EfCourierStore writes both in
// the same SaveChanges transaction; identical GPS replay writes neither.
// Emission fields are a private throttle checkpoint, not assignment authority.
// NTS types never cross into Domain/Application.
public sealed class CourierPosition
{
    public Guid CourierId { get; private set; }
    public Point Location { get; private set; } = null!;
    public DateTimeOffset ObservedAt { get; private set; }
    public Point? LastEventLocation { get; private set; }
    public DateTimeOffset? LastEventAtUtc { get; private set; }
    public Guid? LastEventAssignmentId { get; private set; }
    private CourierPosition() { }
    internal CourierPosition(Courier courier) { CourierId = courier.Id; Update(courier); }
    internal void Update(Courier courier)
    {
        Location = new Point((double)courier.Longitude!.Value, (double)courier.Latitude!.Value) { SRID = 4326 };
        ObservedAt = courier.LocationObservedAt!.Value.ToUniversalTime();
    }

    internal void RecordEmission(Guid assignmentId, DateTimeOffset now)
    {
        LastEventLocation = (Point)Location.Copy();
        LastEventAtUtc = now.ToUniversalTime();
        LastEventAssignmentId = assignmentId;
    }
}
