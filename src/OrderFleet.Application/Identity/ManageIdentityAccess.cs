using OrderFleet.Application.Clock;
using OrderFleet.Application.Errors;

namespace OrderFleet.Application.Identity;

public interface IIdentityAdministrationStore
{
    Task ChangeRoleAsync(Guid actorId, Guid targetId, string role, bool grant, DateTimeOffset now, CancellationToken ct);
    Task ChangeMembershipAsync(Guid actorId, Guid restaurantId, Guid targetId, bool grant, DateTimeOffset now, CancellationToken ct);
}

public sealed class ManageIdentityAccess(IIdentityAdministrationStore store, IClock clock)
{
    public Task RoleAsync(Actor actor, Guid targetId, string role, bool grant, CancellationToken ct)
    {
        if (!actor.IsInRole(OrderFleetRoles.Admin) || actor.Id == targetId) throw new AccessDeniedException();
        if (targetId == Guid.Empty || role is not (OrderFleetRoles.RestaurantOwner or OrderFleetRoles.RestaurantStaff or OrderFleetRoles.Courier))
            throw new RequestValidationException("Unsupported role or user.");
        return store.ChangeRoleAsync(actor.Id, targetId, role, grant, clock.UtcNow, ct);
    }

    public Task MembershipAsync(Actor actor, Guid restaurantId, Guid targetId, bool grant, CancellationToken ct)
    {
        if (!actor.IsInRole(OrderFleetRoles.RestaurantOwner) || actor.Id == targetId) throw new AccessDeniedException();
        if (restaurantId == Guid.Empty || targetId == Guid.Empty) throw new RequestValidationException("Restaurant and user are required.");
        return store.ChangeMembershipAsync(actor.Id, restaurantId, targetId, grant, clock.UtcNow, ct);
    }
}
