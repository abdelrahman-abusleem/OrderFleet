using Microsoft.EntityFrameworkCore;
using OrderFleet.Application.Couriers;
using OrderFleet.Application.Dispatch;
using OrderFleet.Application.Errors;
using OrderFleet.Domain.Orders;
using OrderFleet.Domain.Payments;
using OrderFleet.Infrastructure.Dispatch;
using OrderFleet.Application.Messaging;
using OrderFleet.Application.Orders;

namespace OrderFleet.Infrastructure.Persistence;

public sealed class EfDispatchStore(IDbContextFactory<OrderFleetDbContext> contexts, CourierPolicy couriers) : IDispatchStore
{
    private const string Consumer = ConsumerNames.Dispatch;

    public async Task<DispatchOutcome> TryAssignAsync(Guid orderId, Guid? messageId, DateTimeOffset now, DispatchPolicy policy, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var order = await LockOrderAsync(context, orderId, ct);
        if (messageId.HasValue && await context.InboxMessages.AnyAsync(x => x.MessageId == messageId && x.Consumer == Consumer, ct))
            return DispatchOutcome.AlreadyProcessed;
        var outcome = DispatchOutcome.NotEligible;
        if (order is not null && order.Status == OrderStatus.Accepted
            && await context.Payments.AnyAsync(x => x.OrderId == orderId && x.State == PaymentState.Captured
                && x.Amount == order.TotalAmount && x.Currency == order.Currency, ct))
            outcome = await AssignEligibleAsync(context, order, now, policy, ct);
        if (messageId.HasValue) context.InboxMessages.Add(new(messageId.Value, Consumer, now));
        await new EfOrderingStore(context).SaveAsync(ct);
        await transaction.CommitAsync(ct);
        order?.ClearDomainEvents();
        return outcome;
    }

    private async Task<DispatchOutcome> AssignEligibleAsync(OrderFleetDbContext context, Order order, DateTimeOffset now,
        DispatchPolicy policy, CancellationToken ct)
    {
        var job = await context.DispatchJobs.SingleOrDefaultAsync(x => x.OrderId == order.Id, ct);
        if (job is null) { job = new(order.Id, now); context.DispatchJobs.Add(job); }
        if (job.Status == DispatchJobStatuses.Exhausted) return DispatchOutcome.Exhausted;
        if (job.Status == DispatchJobStatuses.Assigned) job.Restart(now);
        if (job.AssignmentCount >= policy.MaxAssignments)
        {
            job.Exhaust(now);
            context.DispatchAttempts.Add(new(order.Id, DispatchAttemptOutcomes.AssignmentLimit, now));
            return DispatchOutcome.Exhausted;
        }
        if (job.NextAttemptAtUtc > now) return DispatchOutcome.NotDue;
        var restaurant = await context.Restaurants.AsNoTracking().SingleAsync(x => x.Id == order.RestaurantId, ct);
        var oldest = now - couriers.LocationFreshness;
        var latest = now + couriers.FutureSkew;
        var candidate = await context.Couriers.FromSqlInterpolated($"""
            SELECT c.*, c.xmin FROM "Couriers" c JOIN "CourierPositions" p ON p."CourierId" = c."Id"
            WHERE c."Status" = 'Available' AND p."ObservedAt" >= {oldest} AND p."ObservedAt" <= {latest}
              AND ST_DWithin(p."Location", ST_SetSRID(ST_MakePoint({(double)restaurant.Longitude}, {(double)restaurant.Latitude}),4326)::geography, {policy.RadiusMeters})
            ORDER BY ST_Distance(p."Location", ST_SetSRID(ST_MakePoint({(double)restaurant.Longitude}, {(double)restaurant.Latitude}),4326)::geography), c."Id"
            LIMIT 1 FOR UPDATE OF c SKIP LOCKED
            """).SingleOrDefaultAsync(ct);
        if (candidate is null)
        {
            job.Record(DispatchAttemptOutcomes.NoCourier, now, policy.MaxAttempts);
            context.DispatchAttempts.Add(new(order.Id, DispatchAttemptOutcomes.NoCourier, now));
            return job.Status == DispatchJobStatuses.Exhausted ? DispatchOutcome.Exhausted : DispatchOutcome.NoCourier;
        }
        var assignment = order.Assign(candidate.Id, now);
        candidate.Reserve(order.Id, assignment);
        context.CourierAssignments.Add(new(assignment, order.Id, candidate.Id, now, policy.PickupTimeout));
        job.Record(DispatchAttemptOutcomes.Assigned, now, policy.MaxAttempts, assignment);
        context.DispatchAttempts.Add(new(order.Id, DispatchAttemptOutcomes.Assigned, now, candidate.Id, assignment));
        new EfOrderingStore(context).RecordAudit(Guid.Empty, order.Id, OrderAuditActions.CourierDispatched, now);
        return DispatchOutcome.Assigned;
    }

    public async Task TransitionAsync(Guid orderId, Guid actorId, Guid assignmentId, CourierOrderAction action,
        bool administrativeRelease, DateTimeOffset now, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var order = await LockOrderAsync(context, orderId, ct) ?? throw new ResourceNotFoundException();
        if (!administrativeRelease && order.CourierId != actorId) throw new ResourceNotFoundException();
        if (order.AssignmentId != assignmentId) throw new OperationConflictException("Assignment is no longer current.");
        var courier = await context.Couriers.FromSqlInterpolated(
            $"SELECT *, xmin FROM \"Couriers\" WHERE \"Id\" = {order.CourierId} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new OperationConflictException("Courier capacity is missing.");
        if (courier.OrderId != order.Id || courier.AssignmentId != assignmentId)
            throw new OperationConflictException("Courier capacity is no longer current.");
        var reservation = await context.CourierAssignments.SingleOrDefaultAsync(x => x.Id == assignmentId && x.OrderId == orderId, ct)
            ?? throw new OperationConflictException("Assignment schedule is missing.");
        if (action == CourierOrderAction.Pickup && now >= reservation.PickupDeadlineUtc)
            throw new OperationConflictException("Pickup deadline has elapsed.");
        switch (action)
        {
            case CourierOrderAction.Pickup: order.MarkPickedUp(courier.Id, assignmentId, now); courier.MarkBusy(orderId, assignmentId); break;
            case CourierOrderAction.Deliver: order.Deliver(courier.Id, assignmentId, now); break;
            case CourierOrderAction.Release: order.ReleaseAssignment(assignmentId, now); break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
        var store = new EfOrderingStore(context);
        store.RecordAudit(actorId, orderId, OrderAuditActions.CourierActionPrefix + action, now);
        await store.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        order.ClearDomainEvents();
    }

    private static Task<Order?> LockOrderAsync(OrderFleetDbContext context, Guid orderId, CancellationToken ct) =>
        context.Orders.FromSqlInterpolated($"SELECT *, xmin FROM \"Orders\" WHERE \"Id\" = {orderId} FOR UPDATE").SingleOrDefaultAsync(ct);
}
