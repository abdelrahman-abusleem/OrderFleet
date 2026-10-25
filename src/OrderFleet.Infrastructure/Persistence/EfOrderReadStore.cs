using Microsoft.EntityFrameworkCore;
using OrderFleet.Application.Orders;
using OrderFleet.Domain.Orders;

namespace OrderFleet.Infrastructure.Persistence;

public sealed class EfOrderReadStore(OrderFleetDbContext context) : IOrderReadStore
{
    public Task<Order?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        context.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<bool> HasRestaurantMembershipAsync(Guid userId, Guid restaurantId, CancellationToken cancellationToken) =>
        context.RestaurantMemberships.AsNoTracking()
            .AnyAsync(x => x.UserId == userId && x.RestaurantId == restaurantId, cancellationToken);
}
