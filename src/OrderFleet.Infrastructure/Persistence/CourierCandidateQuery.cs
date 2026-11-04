using Microsoft.EntityFrameworkCore;
using OrderFleet.Domain.Couriers;

namespace OrderFleet.Infrastructure.Persistence;

internal static class CourierCandidateQuery
{


    internal static Task<Courier?> ClaimNearestAsync(OrderFleetDbContext context, decimal longitude, decimal latitude,
        DateTimeOffset oldest, DateTimeOffset latest, double radiusMeters, CancellationToken ct) =>
        context.Couriers.FromSqlInterpolated($"""
            SELECT c.*, c.xmin FROM "Couriers" c JOIN "CourierPositions" p ON p."CourierId" = c."Id"
            WHERE c."Status" = 'Available' AND p."ObservedAt" >= {oldest} AND p."ObservedAt" <= {latest}
              AND ST_DWithin(p."Location", ST_SetSRID(ST_MakePoint({(double)longitude}, {(double)latitude}),4326)::geography, {radiusMeters})
            ORDER BY ST_Distance(p."Location", ST_SetSRID(ST_MakePoint({(double)longitude}, {(double)latitude}),4326)::geography), c."Id"
            LIMIT 1 FOR UPDATE OF c SKIP LOCKED
            """).SingleOrDefaultAsync(ct);
}
