using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Npgsql;
using OrderFleet.Application.Couriers;
using OrderFleet.Application.Errors;
using OrderFleet.Domain.Couriers;
using OrderFleet.Application.Clock;
using OrderFleet.Application.Messaging;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OrderFleet.Infrastructure.Couriers;

namespace OrderFleet.Infrastructure.Persistence;

public sealed class EfCourierStore(OrderFleetDbContext context, IClock clock, IOptions<CourierLocationEventOptions>? events = null) : ICourierStore
{
    public Task<Courier?> FindAsync(Guid id, CancellationToken cancellationToken) => context.Couriers.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    public void Add(Courier courier) => context.Couriers.Add(courier);
    public async Task SaveAsync(Courier courier, CancellationToken cancellationToken)
    {
        context.ChangeTracker.DetectChanges();
        var observed = context.Entry(courier).Property(x => x.LocationObservedAt).IsModified;
        CourierPosition? position = null;
        if (courier.LocationObservedAt.HasValue)
        {
            position = await context.CourierPositions.SingleOrDefaultAsync(x => x.CourierId == courier.Id, cancellationToken);
            if (position is null) { position = new(courier); context.CourierPositions.Add(position); }
            else position.Update(courier);
        }
        var now = clock.UtcNow;
        if (observed && position is not null && await ShouldEmitAsync(courier, position, now, cancellationToken))
        {
            position.RecordEmission(courier.AssignmentId!.Value, now);
            context.OutboxMessages.Add(new(new IntegrationEvent(Guid.NewGuid(), "courier.location-updated.v1", 1,
                now, courier.Id, System.Diagnostics.Activity.Current?.TraceId.ToString(),
                JsonSerializer.Serialize(new { courierId = courier.Id, observedAt = courier.LocationObservedAt }),
                TraceParent: OrderFleet.Application.Diagnostics.OrderFleetDiagnostics.CurrentTraceParent)));
        }
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_Couriers" or "PK_CourierPositions" })
        {
            throw new OperationConflictException("Courier profile changed concurrently; refresh before retrying.");
        }
    }

    private async Task<bool> ShouldEmitAsync(Courier courier, CourierPosition position, DateTimeOffset now, CancellationToken ct)
    {
        if (!courier.AssignmentId.HasValue || !courier.OrderId.HasValue) return false;
        if (position.LastEventAssignmentId != courier.AssignmentId || position.LastEventAtUtc is null || position.LastEventLocation is null) return true;
        var policy = events?.Value ?? new CourierLocationEventOptions();
        if (now - position.LastEventAtUtc.Value >= TimeSpan.FromSeconds(policy.MinimumIntervalSeconds)) return true;
        var current = position.Location;
        // Geography distance is evaluated by PostGIS in metres, not NTS degrees.
        var distance = await context.CourierPositions.Where(x => x.CourierId == courier.Id)
            .Select(x => x.LastEventLocation!.Distance(current)).SingleAsync(ct);
        return distance >= policy.MinimumDistanceMeters;
    }

    public async Task<IReadOnlyList<NearbyCourier>> NearbyAsync(decimal latitude, decimal longitude, double radiusMeters,
        DateTimeOffset oldestObservation, DateTimeOffset latestObservation, int limit, CancellationToken cancellationToken)
    {
        var origin = new Point((double)longitude, (double)latitude) { SRID = 4326 };
        return await context.CourierPositions.AsNoTracking()
            .Where(x => x.ObservedAt >= oldestObservation && x.ObservedAt <= latestObservation
                && context.Couriers.Any(c => c.Id == x.CourierId && c.Status == CourierStatus.Available)
                && EF.Functions.IsWithinDistance(x.Location, origin, radiusMeters, true))
            .OrderBy(x => x.Location.Distance(origin)).ThenBy(x => x.CourierId).Take(limit)
            .Select(x => new NearbyCourier(x.CourierId, x.Location.Distance(origin), x.ObservedAt)).ToArrayAsync(cancellationToken);
    }
}
