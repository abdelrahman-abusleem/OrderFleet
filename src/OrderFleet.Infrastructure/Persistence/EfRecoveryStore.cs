using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderFleet.Application.Payments;
using OrderFleet.Application.Recovery;
using OrderFleet.Domain.Orders;
using OrderFleet.Domain.Payments;
using OrderFleet.Infrastructure.Dispatch;
using OrderFleet.Application.Messaging;
using OrderFleet.Application.Orders;
using OrderFleet.Application.Errors;

namespace OrderFleet.Infrastructure.Persistence;

public sealed class EfRecoveryStore(IDbContextFactory<OrderFleetDbContext> contexts, IServiceScopeFactory scopes) : IRecoveryStore
{
    public async Task<IReadOnlyList<Guid>> ExpiredOrdersAsync(DateTimeOffset now, int count, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        return await context.OrderDeliveryInformation.Where(x => x.AcceptanceDeadline <= now
            && context.Orders.Any(o => o.Id == x.OrderId && o.Status == OrderStatus.Created)
            && !context.RecoveryWorkFailures.Any(f => f.Operation == RecoveryWork.AcceptanceTimeout && f.ResourceId == x.OrderId
                && f.ResolvedAtUtc == null && (f.DeadLetteredAtUtc != null || f.NextAttemptAtUtc > now)))
            .OrderBy(x => x.AcceptanceDeadline).ThenBy(x => x.OrderId).Select(x => x.OrderId).Take(count).ToArrayAsync(ct);
    }

    public async Task<bool> ExpireOrderAsync(Guid orderId, DateTimeOffset now, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var order = await OrderLocking.LoadForUpdateAsync(context, orderId, ct);
        if (order?.Status != OrderStatus.Created || !await context.OrderDeliveryInformation.AnyAsync(
            x => x.OrderId == orderId && x.AcceptanceDeadline <= now, ct)) return false;
        order.Cancel(CancellationReason.AcceptanceTimedOut, now);
        var store = new EfOrderingStore(context);
        store.RecordAudit(Guid.Empty, orderId, OrderAuditActions.AcceptanceTimedOut, now);
        await store.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        order.ClearDomainEvents();
        return true;
    }

    public async Task<IReadOnlyList<ExpiredAssignment>> ExpiredAssignmentsAsync(DateTimeOffset now, int count, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        return await context.CourierAssignments.Where(x => x.PickedUpAtUtc == null && x.ReleasedAtUtc == null
            && x.DeliveredAtUtc == null && x.PickupDeadlineUtc <= now
            && !context.RecoveryWorkFailures.Any(f => f.Operation == RecoveryWork.AssignmentTimeout && f.ResourceId == x.Id
                && f.ResolvedAtUtc == null && (f.DeadLetteredAtUtc != null || f.NextAttemptAtUtc > now)))
            .OrderBy(x => x.PickupDeadlineUtc).ThenBy(x => x.Id).Select(x => new ExpiredAssignment(x.OrderId, x.Id)).Take(count).ToArrayAsync(ct);
    }

    public async Task<bool> ReleaseExpiredAssignmentAsync(Guid orderId, Guid assignmentId, DateTimeOffset now, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var order = await OrderLocking.LoadForUpdateAsync(context, orderId, ct);
        if (order?.Status != OrderStatus.Assigned || order.AssignmentId != assignmentId
            || !await context.CourierAssignments.AnyAsync(x => x.Id == assignmentId && x.OrderId == orderId
                && x.PickedUpAtUtc == null && x.ReleasedAtUtc == null && x.PickupDeadlineUtc <= now, ct)) return false;
        order.ReleaseAssignment(assignmentId, now);
        var store = new EfOrderingStore(context);
        store.RecordAudit(Guid.Empty, orderId, OrderAuditActions.PickupReservationExpired, now);
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
                    || context.DispatchJobs.Any(j => j.OrderId == x.Id && j.Status == DispatchJobStatuses.Pending && j.NextAttemptAtUtc <= now))
                && !context.RecoveryWorkFailures.Any(f => f.Operation == RecoveryWork.DispatchRetry && f.ResourceId == x.Id
                    && f.ResolvedAtUtc == null && (f.DeadLetteredAtUtc != null || f.NextAttemptAtUtc > now)))
            .OrderBy(x => x.AcceptedAt).ThenBy(x => x.Id).Select(x => x.Id).Take(count).ToArrayAsync(ct);
    }

    public async Task<IReadOnlyList<CancelledPayment>> UnsettledCancellationsAsync(DateTimeOffset now, int count, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        return await context.OutboxMessages.Where(x => x.Type == IntegrationEventTypes.OrderCancelled
            && context.Payments.Any(p => p.OrderId == x.AggregateId && p.State != PaymentState.Voided && p.State != PaymentState.Refunded)
            && !context.RecoveryWorkFailures.Any(f => f.Operation == RecoveryWork.PaymentSettlement && f.ResourceId == x.AggregateId
                && f.ResolvedAtUtc == null && (f.DeadLetteredAtUtc != null || f.NextAttemptAtUtc > now)))
            .OrderBy(x => x.Sequence).Select(x => new CancelledPayment(x.AggregateId, x.Id)).Take(count).ToArrayAsync(ct);
    }

    public async Task ReconcileCancellationAsync(CancelledPayment payment, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        try { await scope.ServiceProvider.GetRequiredService<SettleCancelledPayment>().ExecuteAsync(payment.MessageId, payment.OrderId, ct); }
        catch (DbUpdateConcurrencyException) { /* Another worker won; a fresh sweep rechecks durable state. */ }
    }

    public async Task<IReadOnlyList<Guid>> UncertainPaymentsAsync(DateTimeOffset now, int count, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        return await context.Payments.Where(x => x.State == PaymentState.PendingReconciliation
            && !context.RecoveryWorkFailures.Any(f => f.Operation == RecoveryWork.CaptureReconciliation && f.ResourceId == x.OrderId
                && f.ResolvedAtUtc == null && (f.DeadLetteredAtUtc != null || f.NextAttemptAtUtc > now)))
            .OrderBy(x => x.AuthorizedAt).ThenBy(x => x.Id).Select(x => x.OrderId).Take(count).ToArrayAsync(ct);
    }

    public async Task ReconcileCaptureAsync(Guid orderId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        try { await scope.ServiceProvider.GetRequiredService<ReconcilePaymentCapture>().ExecuteAsync(orderId, ct); }
        catch (DbUpdateConcurrencyException) { /* Safe to retry the read-only reconciliation next sweep. */ }
    }

    public async Task<IReadOnlyList<Guid>> OrphanAuthorizationsAsync(DateTimeOffset cutoff, DateTimeOffset now, int count, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        return await context.MockPaymentAuthorizations.Where(x => x.State == ProviderPaymentState.Authorized && x.CreatedAt <= cutoff
            && !context.Payments.Any(p => p.AuthorizationId == x.Id)
            && !context.RecoveryWorkFailures.Any(f => f.Operation == RecoveryWork.OrphanAuthorization && f.ResourceId == x.Id
                && f.ResolvedAtUtc == null && (f.DeadLetteredAtUtc != null || f.NextAttemptAtUtc > now)))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => x.Id).Take(count).ToArrayAsync(ct);
    }

    public async Task RecordFailureAsync(string operation, Guid resourceId, DateTimeOffset now, string errorCode, int maxAttempts, CancellationToken ct)
    {
        if (!RecoveryWork.All.Contains(operation) || errorCode is not (FailureCodes.InvariantViolation or FailureCodes.DependencyOrConflict)
            || maxAttempts is < 1 or > 100) throw new ArgumentException("Invalid recovery failure record.");
        await using var context = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "RecoveryWorkFailures" ("Operation", "ResourceId", "Attempts", "NextAttemptAtUtc")
            VALUES ({operation}, {resourceId}, 0, {now}) ON CONFLICT ("Operation", "ResourceId") DO NOTHING
            """, ct);
        var failure = await context.RecoveryWorkFailures.FromSqlInterpolated($"""
            SELECT * FROM "RecoveryWorkFailures" WHERE "Operation" = {operation} AND "ResourceId" = {resourceId} FOR UPDATE
            """).SingleAsync(ct);
        failure.Record(now, errorCode, maxAttempts);
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task RecordResolvedAsync(string operation, Guid resourceId, DateTimeOffset now, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        await context.RecoveryWorkFailures.Where(x => x.Operation == operation && x.ResourceId == resourceId && x.ResolvedAtUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.ResolvedAtUtc, now), ct);
    }

    public async Task ResolveObsoleteFailuresAsync(DateTimeOffset now, int count, CancellationToken ct)
    {
        await using var context = await contexts.CreateDbContextAsync(ct);
        // Resolve diagnostic claims when a concurrent actor already committed the
        // intended terminal/current-state result after a lost local acknowledgement.
        await context.RecoveryWorkFailures.Where(f => f.ResolvedAtUtc == null && (
                (f.Operation == RecoveryWork.AcceptanceTimeout && !context.Orders.Any(o => o.Id == f.ResourceId && o.Status == OrderStatus.Created))
                || (f.Operation == RecoveryWork.AssignmentTimeout && !context.Orders.Any(o => o.AssignmentId == f.ResourceId && o.Status == OrderStatus.Assigned))
                || (f.Operation == RecoveryWork.DispatchRetry && !context.Orders.Any(o => o.Id == f.ResourceId && o.Status == OrderStatus.Accepted))
                || (f.Operation == RecoveryWork.PaymentSettlement && !context.Payments.Any(p => p.OrderId == f.ResourceId && p.State != PaymentState.Voided && p.State != PaymentState.Refunded))
                || (f.Operation == RecoveryWork.CaptureReconciliation && !context.Payments.Any(p => p.OrderId == f.ResourceId && p.State == PaymentState.PendingReconciliation))
                || (f.Operation == RecoveryWork.OrphanAuthorization && (!context.MockPaymentAuthorizations.Any(a => a.Id == f.ResourceId && a.State == ProviderPaymentState.Authorized)
                    || context.Payments.Any(p => p.AuthorizationId == f.ResourceId)))))
            .OrderBy(f => f.NextAttemptAtUtc).Take(count)
            .ExecuteUpdateAsync(set => set.SetProperty(f => f.ResolvedAtUtc, now), ct);
    }

}
