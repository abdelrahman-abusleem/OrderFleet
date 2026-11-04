using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderFleet.Application.Messaging;
using OrderFleet.Application.Payments;
using OrderFleet.Domain.Orders;
using OrderFleet.Domain.Payments;

namespace OrderFleet.Infrastructure.Persistence;

public sealed class EfPaymentStore(OrderFleetDbContext context) : IPaymentStore
{
    public Task<Payment?> FindAsync(Guid orderId, CancellationToken cancellationToken) =>
        context.Payments.SingleOrDefaultAsync(x => x.OrderId == orderId, cancellationToken);
    public Task<bool> IsCancelledAsync(Guid orderId, CancellationToken cancellationToken) =>
        context.Orders.AnyAsync(x => x.Id == orderId && x.Status == OrderStatus.Cancelled, cancellationToken);
    public Task<bool> IsConsumedAsync(Guid messageId, string consumer, CancellationToken cancellationToken) =>
        context.InboxMessages.AsNoTracking().AnyAsync(x => x.MessageId == messageId && x.Consumer == consumer, cancellationToken);
    public void RecordFact(Payment payment, string type, DateTimeOffset now) =>
        context.OutboxMessages.Add(new(IntegrationEvents.FromPayment(payment, type, now)));
    public void RecordConsumed(Guid messageId, string consumer, DateTimeOffset now) =>
        context.InboxMessages.Add(new(messageId, consumer, now));
    public void RecordAttempt(Payment payment, CaptureClassification classification, DateTimeOffset now) =>
        context.PaymentCaptureAttempts.Add(new(payment, classification, now));

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: DatabaseConstraintNames.InboxIdentity })
        {

            context.ChangeTracker.Clear();
        }
    }
}
