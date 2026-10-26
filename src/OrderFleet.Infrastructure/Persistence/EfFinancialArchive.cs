using Microsoft.EntityFrameworkCore;
using OrderFleet.Application.Clock;
using OrderFleet.Application.Messaging;
using OrderFleet.Application.Payments;
using OrderFleet.Infrastructure.Payments;

namespace OrderFleet.Infrastructure.Persistence;

public sealed class EfFinancialArchive(OrderFleetDbContext context, IClock clock) : IFinancialArchive
{
    public const string Consumer = "financial.archive.v1";
    public async Task StoreAsync(IntegrationEvent envelope, FinancialFact fact, CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var now = clock.UtcNow;
        // A unique key serializes competing deliveries across processes; archive
        // and Inbox commit together. Never update an existing immutable fact.
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "FinancialAuditRecords" ("MessageId", "Type", "Version", "OccurredAtUtc", "PaymentId", "OrderId",
                "AuthorizationId", "Amount", "Currency", "State", "ArchivedAtUtc")
            VALUES ({envelope.MessageId}, {envelope.Type}, {envelope.Version}, {envelope.OccurredAtUtc}, {fact.PaymentId}, {fact.OrderId},
                {fact.AuthorizationId}, {fact.Amount}, {fact.Currency}, {fact.State}, {now}) ON CONFLICT ("MessageId") DO NOTHING
            """, ct);
        var stored = await context.FinancialAuditRecords.AsNoTracking().SingleAsync(x => x.MessageId == envelope.MessageId, ct);
        if (!stored.Matches(envelope, fact)) throw new InvalidDataException("Financial message identity was reused with different content.");
        // Inbox also uses an atomic conflict-safe insert for simultaneous replay.
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "InboxMessages" ("MessageId", "Consumer", "ProcessedAtUtc")
            VALUES ({envelope.MessageId}, {Consumer}, {now}) ON CONFLICT ("MessageId", "Consumer") DO NOTHING
            """, ct);
        await transaction.CommitAsync(ct);
    }
}
