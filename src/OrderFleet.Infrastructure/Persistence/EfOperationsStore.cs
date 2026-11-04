using Microsoft.EntityFrameworkCore;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Operations;
using OrderFleet.Domain.Orders;
using OrderFleet.Domain.Payments;
using OrderFleet.Application.Identity;
using OrderFleet.Infrastructure.Dispatch;

namespace OrderFleet.Infrastructure.Persistence;

public sealed class EfOperationsStore(OrderFleetDbContext context) : IOperationsStore
{
    public Task<bool> IsAdministratorAsync(Guid actorId, CancellationToken ct) =>
        (from membership in context.UserRoles.AsNoTracking()
         join role in context.Roles on membership.RoleId equals role.Id
         where membership.UserId == actorId && role.Name == OrderFleetRoles.Admin
         select membership.UserId).AnyAsync(ct);
    public async Task<OperationsSummary> SummaryAsync(DateTimeOffset now, CancellationToken ct)
    {
        var pending = context.OutboxMessages.AsNoTracking().Where(x => x.PublishedAtUtc == null && x.DeadLetteredAtUtc == null);
        var oldest = await pending.MinAsync(x => (DateTimeOffset?)x.OccurredAtUtc, ct);
        return new(await pending.LongCountAsync(ct), await context.OutboxMessages.LongCountAsync(x => x.DeadLetteredAtUtc != null, ct),
            oldest.HasValue ? Math.Max(0, (now - oldest.Value).TotalSeconds) : 0,
            await context.DispatchJobs.LongCountAsync(x => x.Status == DispatchJobStatuses.Pending, ct),
            await context.DispatchJobs.LongCountAsync(x => x.Status == DispatchJobStatuses.Exhausted, ct),
            await context.Payments.LongCountAsync(x => x.State == PaymentState.PendingReconciliation, ct),
            await context.Payments.LongCountAsync(x => x.State != PaymentState.Voided && x.State != PaymentState.Refunded
                && context.Orders.Any(o => o.Id == x.OrderId && o.Status == OrderStatus.Cancelled), ct),
            await context.RecoveryWorkFailures.LongCountAsync(x => x.ResolvedAtUtc == null && x.DeadLetteredAtUtc != null, ct),
            await context.RecoveryWorkFailures.LongCountAsync(x => x.ResolvedAtUtc == null && x.DeadLetteredAtUtc == null, ct));
    }

    public async Task<IReadOnlyList<OutboxDiagnostic>> OutboxAsync(string state, int offset, int count, CancellationToken ct) =>
        await context.OutboxMessages.AsNoTracking().Where(x => state == "dead" ? x.DeadLetteredAtUtc != null
            : x.PublishedAtUtc == null && x.DeadLetteredAtUtc == null).OrderBy(x => x.Sequence).Skip(offset).Take(count)
            .Select(x => new OutboxDiagnostic(x.Id, x.Type, x.OccurredAtUtc, x.Attempts, x.NextAttemptAtUtc, x.DeadLetteredAtUtc, x.LastErrorCode)).ToArrayAsync(ct);

    public async Task RetryOutboxAsync(Guid id, Guid actorId, string key, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);


        await LockActorAsync(actorId, ct);
        if (await ReplayAsync(actorId, OperationNames.RetryOutbox, key, id, ct)) return;
        var message = await context.OutboxMessages.FromSqlInterpolated(
            $"SELECT * FROM \"OutboxMessages\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw new ResourceNotFoundException();
        if (message.PublishedAtUtc is not null || message.LeasedUntilUtc > now) throw new OperationConflictException("Message cannot be retried while published or actively leased.");
        if (message.DeadLetteredAtUtc is null) throw new OperationConflictException("Only dead messages require operator replay.");
        var attempts = message.Attempts;
        var outcome = message.LastErrorCode;
        message.ResetForRetry(now);
        Record(actorId, OperationNames.RetryOutbox, key, id, now, attempts, outcome);
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task RetryDispatchAsync(Guid orderId, Guid actorId, string key, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockActorAsync(actorId, ct);
        if (await ReplayAsync(actorId, OperationNames.RetryDispatch, key, orderId, ct)) return;
        var order = await OrderLocking.LoadForUpdateAsync(context, orderId, ct) ?? throw new ResourceNotFoundException();
        if (order.Status != OrderStatus.Accepted || !await context.Payments.AnyAsync(x => x.OrderId == orderId && x.State == PaymentState.Captured, ct))
            throw new OperationConflictException("Only accepted captured orders can restart dispatch.");
        var job = await context.DispatchJobs.SingleOrDefaultAsync(x => x.OrderId == orderId, ct);
        var attempts = job?.Attempts;
        var outcome = job?.Status;
        if (job is null) context.DispatchJobs.Add(new(orderId, now));
        else job.RestartForOperator(now);
        Record(actorId, OperationNames.RetryDispatch, key, orderId, now, attempts, outcome);
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private Task<int> LockActorAsync(Guid actorId, CancellationToken ct) => context.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\" = {actorId} FOR UPDATE", ct);
    private async Task<bool> ReplayAsync(Guid actorId, string operation, string key, Guid targetId, CancellationToken ct)
    {
        var previous = await context.OperationRequests.AsNoTracking().SingleOrDefaultAsync(x => x.ActorId == actorId && x.Operation == operation && x.Key == key, ct);
        if (previous is null) return false;
        if (previous.TargetId != targetId) throw new OperationConflictException("Operation key was already used for another target.");
        return true;
    }
    private void Record(Guid actorId, string operation, string key, Guid targetId, DateTimeOffset now, int? previousAttempts, string? previousOutcome)
    {
        context.OperationRequests.Add(new(actorId, operation, key, targetId, now));
        context.OperationAudits.Add(new(actorId, operation, targetId, now, previousAttempts, previousOutcome));
    }

    public async Task<IReadOnlyList<RecoveryDiagnostic>> RecoveryFailuresAsync(string state, int offset, int count, CancellationToken ct) =>
        await context.RecoveryWorkFailures.AsNoTracking().Where(x => x.ResolvedAtUtc == null
            && (state == "dead" ? x.DeadLetteredAtUtc != null : x.DeadLetteredAtUtc == null))
            .OrderBy(x => x.NextAttemptAtUtc).ThenBy(x => x.ResourceId).ThenBy(x => x.Operation).Skip(offset).Take(count)
            .Select(x => new RecoveryDiagnostic(x.Operation, x.ResourceId, x.Attempts, x.NextAttemptAtUtc, x.DeadLetteredAtUtc, x.LastErrorCode)).ToArrayAsync(ct);

    public async Task RetryRecoveryAsync(string operation, Guid resourceId, Guid actorId, string key, DateTimeOffset now, CancellationToken ct)
    {
        var scope = OperationNames.RetryRecoveryPrefix + operation;
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockActorAsync(actorId, ct);
        if (await ReplayAsync(actorId, scope, key, resourceId, ct)) return;
        var failure = await context.RecoveryWorkFailures.FromSqlInterpolated($"""
            SELECT * FROM "RecoveryWorkFailures" WHERE "Operation" = {operation} AND "ResourceId" = {resourceId} FOR UPDATE
            """).SingleOrDefaultAsync(ct) ?? throw new ResourceNotFoundException();
        if (failure.ResolvedAtUtc.HasValue || !failure.DeadLetteredAtUtc.HasValue)
            throw new OperationConflictException("Only unresolved dead recovery work requires operator retry.");
        var attempts = failure.Attempts;
        var outcome = failure.LastErrorCode;
        failure.Retry(now);
        Record(actorId, scope, key, resourceId, now, attempts, outcome);
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
