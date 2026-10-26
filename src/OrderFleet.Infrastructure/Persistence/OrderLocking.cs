using Microsoft.EntityFrameworkCore;
using OrderFleet.Domain.Orders;

namespace OrderFleet.Infrastructure.Persistence;

internal static class OrderLocking
{
    // Caller owns the existing transaction and any multi-order sorting. Loading
    // includes xmin; locking a tracked root must preserve its original CAS token.
    internal static Task<Order?> LoadForUpdateAsync(OrderFleetDbContext context, Guid id, CancellationToken ct) =>
        context.Orders.FromSqlInterpolated($"SELECT *, xmin FROM \"Orders\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);

    internal static Task LockTrackedAsync(OrderFleetDbContext context, Guid id, CancellationToken ct) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Orders\" WHERE \"Id\" = {id} FOR UPDATE", ct);
}
