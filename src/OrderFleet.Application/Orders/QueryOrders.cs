using OrderFleet.Application.Catalog;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Identity;

namespace OrderFleet.Application.Orders;

public sealed class QueryOrders(IOrderQueryStore store, IRestaurantAccess access, GetOrder getOrder)
{
    public async Task<DeliveryDetails> DeliveryAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        _ = await getOrder.ExecuteAsync(id, actor, cancellationToken) ?? throw new ResourceNotFoundException();
        return await store.DeliveryAsync(id, cancellationToken) ?? throw new ResourceNotFoundException();
    }

    public async Task<IReadOnlyList<OrderDetails>> ListAsync(Actor actor, Guid? restaurantId, int page, int size,
        CancellationToken cancellationToken)
    {
        var offset = BrowseCatalog.Offset(page, size);
        Guid? customerId = actor.Id;
        if (restaurantId.HasValue)
        {
            if ((!actor.IsInRole(OrderFleetRoles.RestaurantOwner) && !actor.IsInRole(OrderFleetRoles.RestaurantStaff))
                || !await access.HasMembershipAsync(actor.Id, restaurantId.Value, cancellationToken))
                throw new ResourceNotFoundException();
            customerId = null;
        }
        else if (!actor.IsInRole(OrderFleetRoles.Customer)) throw new AccessDeniedException();
        return (await store.ListAsync(customerId, restaurantId, offset, size, cancellationToken))
            .Select(GetOrder.Describe).ToArray();
    }
}
