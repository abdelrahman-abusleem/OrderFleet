using Microsoft.EntityFrameworkCore;
using OrderFleet.Application.Orders;
using OrderFleet.Domain.Orders;

namespace OrderFleet.Infrastructure.Persistence;

public sealed class EfOrderReadStore(OrderFleetDbContext context) : IOrderReadStore
{
    public Task<Order?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        context.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

}
