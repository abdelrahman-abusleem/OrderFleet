using OrderFleet.Application.Catalog;
using OrderFleet.Application.Identity;

namespace OrderFleet.Application.Orders;

public sealed class OrderAccessPolicy(IRestaurantAccess access)
{
    public async Task<bool> CanReadAsync(Guid customerId, Guid restaurantId, Guid? courierId, Actor actor, CancellationToken ct)
    {
        if (actor.IsInRole(OrderFleetRoles.Admin)) return true;
        if (actor.IsInRole(OrderFleetRoles.Customer) && customerId == actor.Id) return true;
        if (actor.IsInRole(OrderFleetRoles.Courier) && courierId == actor.Id) return true;
        return (actor.IsInRole(OrderFleetRoles.RestaurantStaff) || actor.IsInRole(OrderFleetRoles.RestaurantOwner))
            && await access.HasMembershipAsync(actor.Id, restaurantId, ct);
    }
}
