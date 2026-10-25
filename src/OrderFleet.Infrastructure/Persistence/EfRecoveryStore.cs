using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderFleet.Application.Payments;
using OrderFleet.Application.Recovery;
using OrderFleet.Domain.Orders;
using OrderFleet.Domain.Payments;

namespace OrderFleet.Infrastructure.Persistence;

public sealed class EfRecoveryStore(IDbContextFactory<OrderFleetDbContext> contexts, IServiceScopeFactory scopes) : IRecoveryStore
{
    public async Task<IReadOnlyList<Guid>> ExpiredOrdersAsync(DateTimeOffset now, int count, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        return await context.OrderDeliveryInformation.Where(x => x.AcceptanceDeadline <= now
            && context.Orders.Any(o => o.Id == x.OrderId && o.Status == OrderStatus.Created))
            .OrderBy(x => x.AcceptanceDeadline).ThenBy(x => x.OrderId).Select(x => x.OrderId).Take(count).ToArrayAsync(ct);
    }

    public async Task<bool> ExpireOrderAsync(Guid orderId, DateTimeOffset now, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var order = await LockOrderAsync(context, orderId, ct);
        if (order?.Status != OrderStatus.Created || !await context.OrderDeliveryInformation.AnyAsync(
            x => x.OrderId == orderId && x.AcceptanceDeadline <= now, ct)) return false;
        order.Cancel(CancellationReason.AcceptanceTimedOut, now);
        var store = new EfOrderingStore(context);
        store.RecordAudit(Guid.Empty, orderId, "AcceptanceTimedOut", now);
        await store.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        order.ClearDomainEvents();
        return true;
    }

    public async Task<IReadOnlyList<ExpiredAssignment>> ExpiredAssignmentsAsync(DateTimeOffset now, int count, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        return await context.CourierAssignments.Where(x => x.PickedUpAtUtc == null && x.ReleasedAtUtc == null
            && x.DeliveredAtUtc == null && x.PickupDeadlineUtc <= now)
            .OrderBy(x => x.PickupDeadlineUtc).ThenBy(x => x.Id).Select(x => new ExpiredAssignment(x.OrderId, x.Id)).Take(count).ToArrayAsync(ct);
    }

    public async Task<bool> ReleaseExpiredAssignmentAsync(Guid orderId, Guid assignmentId, DateTimeOffset now, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var order = await LockOrderAsync(context, orderId, ct);
        if (order?.Status != OrderStatus.Assigned || order.AssignmentId != assignmentId
            || !await context.CourierAssignments.AnyAsync(x => x.Id == assignmentId && x.OrderId == orderId
                && x.PickedUpAtUtc == null && x.ReleasedAtUtc == null && x.PickupDeadlineUtc <= now, ct)) return false;
        order.ReleaseAssignment(assignmentId, now);
        var store = new EfOrderingStore(context);
        store.RecordAudit(Guid.Empty, orderId, "PickupReservationExpired", now);
        await store.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        order.ClearDomainEvents();
        return true;
    }

    public async Task<IReadOnlyList<Guid>> DueDispatchAsync(DateTimeOffset now, int count, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        return await context.Orders.Where(x => x.Status == OrderStatus.Accepted
                && context.Payments.Any(p => p.OrderId == x.Id && p.State == PaymentState.Captured)
                && (!context.DispatchJobs.Any(j => j.OrderId == x.Id)
                    || context.DispatchJobs.Any(j => j.OrderId == x.Id && j.Status == "Pending" && j.NextAttemptAtUtc <= now)))
            .OrderBy(x => x.AcceptedAt).ThenBy(x => x.Id).Select(x => x.Id).Take(count).ToArrayAsync(ct);
    }

    public async Task<IReadOnlyList<CancelledPayment>> UnsettledCancellationsAsync(int count, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        return await context.OutboxMessages.Where(x => x.Type == "order.cancelled.v1"
            && context.Payments.Any(p => p.OrderId == x.AggregateId && p.State != PaymentState.Voided && p.State != PaymentState.Refunded))
            .OrderBy(x => x.Sequence).Select(x => new CancelledPayment(x.AggregateId, x.Id)).Take(count).ToArrayAsync(ct);
    }

    public async Task ReconcileCancellationAsync(CancelledPayment payment, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        try { await scope.ServiceProvider.GetRequiredService<SettleCancelledPayment>().ExecuteAsync(payment.MessageId, payment.OrderId, ct); }
        catch (DbUpdateConcurrencyException) { /* Another worker won; a fresh sweep rechecks durable state. */ }
    }

    public async Task<IReadOnlyList<Guid>> UncertainPaymentsAsync(int count, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        return await context.Payments.Where(x => x.State == PaymentState.PendingReconciliation)
            .OrderBy(x => x.AuthorizedAt).ThenBy(x => x.Id).Select(x => x.OrderId).Take(count).ToArrayAsync(ct);
    }

    public async Task ReconcileCaptureAsync(Guid orderId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        try { await scope.ServiceProvider.GetRequiredService<ReconcilePaymentCapture>().ExecuteAsync(orderId, ct); }
        catch (DbUpdateConcurrencyException) { /* Safe to retry the read-only reconciliation next sweep. */ }
    }

    public async Task<IReadOnlyList<Guid>> OrphanAuthorizationsAsync(DateTimeOffset cutoff, int count, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        return await context.MockPaymentAuthorizations.Where(x => x.State == ProviderPaymentState.Authorized && x.CreatedAt <= cutoff
            && !context.Payments.Any(p => p.AuthorizationId == x.Id)).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => x.Id).Take(count).ToArrayAsync(ct);
    }

    private static Task<Order?> LockOrderAsync(OrderFleetDbContext context, Guid id, CancellationToken ct) =>
        context.Orders.FromSqlInterpolated($"SELECT *, xmin FROM \"Orders\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
}
