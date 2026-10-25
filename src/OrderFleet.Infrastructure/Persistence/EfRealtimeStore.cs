using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderFleet.Application.Identity;
using OrderFleet.Application.Realtime;
using OrderFleet.Domain.Orders;

namespace OrderFleet.Infrastructure.Persistence;

public sealed class EfRealtimeStore(OrderFleetDbContext context) : IRealtimeStore
{
    private const string Consumer = "realtime.v1";

    public async Task<Actor> RefreshActorAsync(Actor tokenActor, CancellationToken ct)
    {
        var roles = await (from membership in context.UserRoles.AsNoTracking()
                           join role in context.Roles on membership.RoleId equals role.Id
                           where membership.UserId == tokenActor.Id && role.Name != null
                           select role.Name!).ToArrayAsync(ct);
        return new(tokenActor.Id, roles.Where(tokenActor.Roles.Contains).ToHashSet(StringComparer.Ordinal));
    }

    public async Task<RealtimeOrderSnapshot?> ReadAsync(Guid orderId, CancellationToken ct)
    {
        var order = await context.Orders.AsNoTracking().Where(x => x.Id == orderId)
            .Select(x => new
            {
                x.Id,
                x.CustomerId,
                x.RestaurantId,
                x.Status,
                x.CourierId,
                x.AssignmentId,
                Sequence = EF.Property<long>(x, "LastEventSequence")
            }).SingleOrDefaultAsync(ct);
        if (order is null) return null;
        CourierLocationUpdate? location = null;
        if (order.CourierId.HasValue && order.AssignmentId.HasValue && order.Status is OrderStatus.Assigned or OrderStatus.PickedUp)
        {
            var courier = await context.Couriers.AsNoTracking().Where(x => x.Id == order.CourierId && x.OrderId == order.Id && x.AssignmentId == order.AssignmentId)
                .Select(x => new { x.Latitude, x.Longitude, x.LocationObservedAt }).SingleOrDefaultAsync(ct);
            if (courier?.LocationObservedAt is not null && courier.Latitude.HasValue && courier.Longitude.HasValue)
                location = new(order.Id, order.CourierId.Value, order.AssignmentId.Value, courier.Latitude.Value, courier.Longitude.Value, courier.LocationObservedAt.Value);
        }
        return new(order.CustomerId, order.RestaurantId, new(order.Id, order.Sequence, order.Status.ToString(), order.CourierId, order.AssignmentId), location);
    }

    public Task<Guid?> ActiveOrderAsync(Guid courierId, CancellationToken ct) => context.Orders.AsNoTracking()
        .Where(x => x.CourierId == courierId && (x.Status == OrderStatus.Assigned || x.Status == OrderStatus.PickedUp))
        .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
    public Task<bool> IsProcessedAsync(Guid messageId, CancellationToken ct) => context.InboxMessages.AnyAsync(x => x.MessageId == messageId && x.Consumer == Consumer, ct);
    public async Task MarkProcessedAsync(Guid messageId, DateTimeOffset now, CancellationToken ct)
    {
        context.InboxMessages.Add(new(messageId, Consumer, now));
        try { await context.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_InboxMessages" })
        { context.ChangeTracker.Clear(); }
    }
}
