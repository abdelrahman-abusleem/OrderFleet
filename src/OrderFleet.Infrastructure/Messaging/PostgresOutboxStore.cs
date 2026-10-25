using Microsoft.EntityFrameworkCore;
using OrderFleet.Application.Messaging;
using OrderFleet.Infrastructure.Persistence;

namespace OrderFleet.Infrastructure.Messaging;

public sealed class PostgresOutboxStore(IDbContextFactory<OrderFleetDbContext> contexts) : IOutboxStore
{
    public async Task<IReadOnlyList<OutboxLease>> ClaimAsync(int count, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        if (count is < 1 or > 100 || leaseDuration < TimeSpan.FromSeconds(1) || leaseDuration > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(count));
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var leaseId = Guid.NewGuid();
        var until = now + leaseDuration;
        // One DB statement claims without a process lock. SKIP LOCKED allows several
        // workers to make progress; token and deadline fence all completion writes.
        var rows = await context.OutboxMessages.FromSqlInterpolated($"""
            WITH candidates AS (
                SELECT "Id" FROM "OutboxMessages"
                WHERE "PublishedAtUtc" IS NULL AND "DeadLetteredAtUtc" IS NULL
                  AND "NextAttemptAtUtc" <= {now} AND ("LeasedUntilUtc" IS NULL OR "LeasedUntilUtc" <= {now})
                ORDER BY "Sequence" LIMIT {count} FOR UPDATE SKIP LOCKED
            )
            UPDATE "OutboxMessages" AS target SET "LeaseId" = {leaseId}, "LeasedUntilUtc" = {until}, "Attempts" = target."Attempts" + 1
            FROM candidates WHERE target."Id" = candidates."Id" RETURNING target.*
            """).AsNoTracking().ToArrayAsync(cancellationToken);
        return rows.OrderBy(x => x.Sequence).Select(x => new OutboxLease(leaseId,
            new(x.Id, x.Type, x.Version, x.OccurredAtUtc, x.AggregateId, x.CorrelationId, x.PayloadJson, x.AggregateSequence, x.TraceParent), x.Attempts)).ToArray();
    }

    public async Task<bool> CompleteAsync(OutboxLease lease, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        return await context.OutboxMessages.Where(x => x.Id == lease.Message.MessageId && x.LeaseId == lease.LeaseId
                && x.LeasedUntilUtc > now && x.PublishedAtUtc == null && x.DeadLetteredAtUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.PublishedAtUtc, now).SetProperty(x => x.LeaseId, (Guid?)null)
                .SetProperty(x => x.LeasedUntilUtc, (DateTimeOffset?)null).SetProperty(x => x.LastErrorCode, (string?)null), cancellationToken) == 1;
    }

    public async Task<bool> FailAsync(OutboxLease lease, DateTimeOffset now, string errorCode, int maxAttempts, CancellationToken cancellationToken)
    {
        if (maxAttempts is < 1 or > 100 || errorCode is not ("BrokerUnavailable" or "InvalidEnvelope")) throw new ArgumentException("Invalid retry policy.");
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var next = now.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(lease.Attempts, 8))));
        DateTimeOffset? dead = lease.Attempts >= maxAttempts ? now : null;
        return await context.OutboxMessages.Where(x => x.Id == lease.Message.MessageId && x.LeaseId == lease.LeaseId
                && x.LeasedUntilUtc > now && x.PublishedAtUtc == null && x.DeadLetteredAtUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.NextAttemptAtUtc, next).SetProperty(x => x.DeadLetteredAtUtc, dead)
                .SetProperty(x => x.LeaseId, (Guid?)null).SetProperty(x => x.LeasedUntilUtc, (DateTimeOffset?)null)
                .SetProperty(x => x.LastErrorCode, errorCode), cancellationToken) == 1;
    }
}
