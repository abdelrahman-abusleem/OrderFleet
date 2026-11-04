using Microsoft.EntityFrameworkCore;
using OrderFleet.Domain.Orders;

namespace OrderFleet.Infrastructure.Persistence;

internal static class OrderLocking
{


    internal static Task<Order?> LoadForUpdateAsync(OrderFleetDbContext context, Guid id, CancellationToken ct) =>
        context.Orders.FromSqlInterpolated($"SELECT *, xmin FROM \"Orders\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);

    internal static Task LockTrackedAsync(OrderFleetDbContext context, Guid id, CancellationToken ct) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Orders\" WHERE \"Id\" = {id} FOR UPDATE", ct);
}
