using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderFleet.Application.Clock;
using OrderFleet.Application.Messaging;
using OrderFleet.Domain.Orders;
using OrderFleet.Domain.Payments;
using OrderFleet.Infrastructure.Persistence;

namespace OrderFleet.Infrastructure.Maintenance;

public sealed record CleanupResult(long PublishedOutbox, long ResolvedFailures, long DispatchAttempts);

public sealed class ExpiredDataCleanup(IDbContextFactory<OrderFleetDbContext> contexts, IClock clock, IOptions<CleanupOptions> options)
{
    public async Task<CleanupResult> RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var settings = options.Value;
        var now = clock.UtcNow;
        var outboxCutoff = now.AddDays(-settings.PublishedOutboxDays);
        var recoveryCutoff = now.AddDays(-settings.ResolvedRecoveryDays);
        var dispatchCutoff = now.AddDays(-settings.FinishedDispatchDays);
        await using var db = await contexts.CreateDbContextAsync(ct);



        var outbox = db.OutboxMessages.Where(x => x.PublishedAtUtc < outboxCutoff && x.LeaseId == null && x.DeadLetteredAtUtc == null
            && ((x.Type.StartsWith(IntegrationEventTypes.PaymentPrefix)
                && db.FinancialAuditRecords.Any(a => a.MessageId == x.Id)
                && db.InboxMessages.Any(i => i.MessageId == x.Id && i.Consumer == ConsumerNames.FinancialArchive))
                || ((x.Type.StartsWith(IntegrationEventTypes.OrderPrefix) || x.Type == IntegrationEventTypes.CourierLocationUpdated)
                    && db.InboxMessages.Any(i => i.MessageId == x.Id && i.Consumer == ConsumerNames.Realtime)
                    && ((x.Type != IntegrationEventTypes.OrderAccepted && x.Type != IntegrationEventTypes.AssignmentReleased)
                        || db.InboxMessages.Any(i => i.MessageId == x.Id && i.Consumer == ConsumerNames.Dispatch))
                    && (x.Type != IntegrationEventTypes.OrderCancelled || (db.InboxMessages.Any(i => i.MessageId == x.Id && i.Consumer == ConsumerNames.CancelledPayments)
                        && db.Payments.Any(p => p.OrderId == x.AggregateId && (p.State == PaymentState.Voided || p.State == PaymentState.Refunded)))))));
        var outboxKeys = outbox.OrderBy(x => x.PublishedAtUtc).ThenBy(x => x.Id).Take(settings.BatchSize).Select(x => x.Id);
        var published = await DrainAsync(() => outbox.AnyAsync(ct),
            () => outbox.Where(x => outboxKeys.Contains(x.Id)).ExecuteDeleteAsync(ct), ct);

        var failures = db.RecoveryWorkFailures.Where(x => x.ResolvedAtUtc < recoveryCutoff);
        var failureKeys = failures.OrderBy(x => x.ResolvedAtUtc).ThenBy(x => x.Operation).ThenBy(x => x.ResourceId).Take(settings.BatchSize);


        var resolved = await DrainAsync(() => failures.AnyAsync(ct),
            () => failures.Where(x => failureKeys.Any(k => k.Operation == x.Operation && k.ResourceId == x.ResourceId)).ExecuteDeleteAsync(ct), ct);

        var attempts = db.DispatchAttempts.Where(x => x.OccurredAtUtc < dispatchCutoff && db.Orders.Any(o => o.Id == x.OrderId
            && ((o.Status == OrderStatus.Delivered && o.DeliveredAt < dispatchCutoff)
                || (o.Status == OrderStatus.Cancelled && o.CancelledAt < dispatchCutoff))));
        var attemptKeys = attempts.OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Id).Take(settings.BatchSize).Select(x => x.Id);
        var finished = await DrainAsync(() => attempts.AnyAsync(ct),
            () => attempts.Where(x => attemptKeys.Contains(x.Id)).ExecuteDeleteAsync(ct), ct);
        return new(published, resolved, finished);
    }

    private static async Task<long> DrainAsync(Func<Task<bool>> hasRows, Func<Task<int>> deleteBatch, CancellationToken ct)
    {
        long deleted = 0;
        while (await hasRows())
        {
            ct.ThrowIfCancellationRequested();
            deleted += await deleteBatch();
        }
        return deleted;
    }
}
