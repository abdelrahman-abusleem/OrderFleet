using NetTopologySuite.Geometries;
using OrderFleet.Domain.Couriers;

namespace OrderFleet.Infrastructure.Couriers;

// Spatial storage adapter; NTS types never cross into Domain/Application.
public sealed class CourierPosition
{
    public Guid CourierId { get; private set; }
    public Point Location { get; private set; } = null!;
    public DateTimeOffset ObservedAt { get; private set; }
    private CourierPosition() { }
    internal CourierPosition(Courier courier) { CourierId = courier.Id; Update(courier); }
    internal void Update(Courier courier)
    {
        Location = new Point((double)courier.Longitude!.Value, (double)courier.Latitude!.Value) { SRID = 4326 };
        ObservedAt = courier.LocationObservedAt!.Value.ToUniversalTime();
    }
}
