using OrderFleet.Application.Catalog;
using OrderFleet.Application.Clock;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Identity;
using OrderFleet.Application.Orders;

namespace OrderFleet.Application.Couriers;

public sealed class FindNearbyCouriers(ICourierStore couriers, ICatalogStore catalog, IRestaurantAccess access,
    IClock clock, CourierPolicy policy)
{
    public async Task<IReadOnlyList<NearbyCourier>> ExecuteAsync(Actor actor, Guid restaurantId,
        double radiusMeters, int limit, CancellationToken cancellationToken)
    {
        if (!actor.IsInRole(OrderFleetRoles.Admin))
        {
            if (!actor.IsInRole(OrderFleetRoles.RestaurantOwner) && !actor.IsInRole(OrderFleetRoles.RestaurantStaff))
                throw new AccessDeniedException();
            if (!await access.HasMembershipAsync(actor.Id, restaurantId, cancellationToken)) throw new ResourceNotFoundException();
        }
        if (!double.IsFinite(radiusMeters) || radiusMeters <= 0 || radiusMeters > policy.MaxRadiusMeters || limit is < 1 or > 50)
            throw new RequestValidationException("Discovery radius or limit is outside allowed bounds.");
        var restaurant = await catalog.RestaurantAsync(restaurantId, cancellationToken) ?? throw new ResourceNotFoundException();
        var now = clock.UtcNow;
        return await couriers.NearbyAsync(restaurant.Latitude, restaurant.Longitude, radiusMeters,
            now - policy.LocationFreshness, now + policy.FutureSkew, limit, cancellationToken);
    }
}
