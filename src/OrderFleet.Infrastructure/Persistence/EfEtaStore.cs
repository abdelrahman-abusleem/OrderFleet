using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using OrderFleet.Application.Eta;

namespace OrderFleet.Infrastructure.Persistence;

public sealed class EfEtaStore(OrderFleetDbContext context) : IEtaStore
{
    public async Task<EtaInputs?> ReadAsync(Guid orderId, CancellationToken ct)
    {
        var source = await (from order in context.Orders.AsNoTracking()
                            join restaurant in context.Restaurants on order.RestaurantId equals restaurant.Id
                            join delivery in context.OrderDeliveryInformation on order.Id equals delivery.OrderId
                            where order.Id == orderId
                            select new
                            {
                                order.Status,
                                order.CourierId,
                                order.AcceptedAt,
                                PickupLatitude = restaurant.Latitude,
                                PickupLongitude = restaurant.Longitude,
                                DestinationLatitude = delivery.Latitude,
                                DestinationLongitude = delivery.Longitude
                            }).SingleOrDefaultAsync(ct);
        if (source is null) return null;
        var pickup = new Point((double)source.PickupLongitude, (double)source.PickupLatitude) { SRID = 4326 };
        var destination = new Point((double)source.DestinationLongitude, (double)source.DestinationLatitude) { SRID = 4326 };
        // Execute on PostGIS geography, never NTS client-side degrees. Only derived
        // distances cross the Application port; precise destination stays private.
        var route = await context.Database.SqlQuery<double>($"""
            SELECT ST_Distance({pickup}::geography, {destination}::geography) AS "Value"
            """).SingleAsync(ct);
        var courier = source.CourierId.HasValue ? await context.CourierPositions.AsNoTracking()
            .Where(x => x.CourierId == source.CourierId)
            .Select(x => new
            {
                ToPickup = EF.Functions.Distance(x.Location, pickup, true),
                ToDestination = EF.Functions.Distance(x.Location, destination, true),
                x.ObservedAt
            }).SingleOrDefaultAsync(ct) : null;
        return new(source.Status.ToString(), route, courier?.ToPickup, courier?.ToDestination, courier?.ObservedAt, source.AcceptedAt);
    }
}
