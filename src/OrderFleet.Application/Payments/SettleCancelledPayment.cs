using OrderFleet.Application.Clock;
using OrderFleet.Application.Errors;
using OrderFleet.Domain.Payments;

namespace OrderFleet.Application.Payments;

public sealed class SettleCancelledPayment(IPaymentStore store, IPaymentProvider provider, IClock clock)
{
    public async Task ExecuteAsync(Guid messageId, Guid orderId, CancellationToken cancellationToken)
    {
        const string consumer = "payments.cancelled.v1";
        if (await store.IsConsumedAsync(messageId, consumer, cancellationToken)) return;
        if (!await store.IsCancelledAsync(orderId, cancellationToken))
            throw new OperationConflictException("Cancellation fact does not match durable order state.");
        var payment = await store.FindAsync(orderId, cancellationToken) ?? throw new ResourceNotFoundException();
        // Provider state resolves capture-before-order-commit crashes and cancel/capture races.
        var settled = await provider.SettleAsync(payment.AuthorizationId, cancellationToken);
        if (settled.State == ProviderPaymentState.Refunded)
        {
            if (payment.State is PaymentState.Authorized or PaymentState.Failed or PaymentState.PendingReconciliation)
                payment.RecordCapture(settled.CapturedAt ?? settled.OccurredAt);
            if (payment.RecordRefund(settled.OccurredAt)) store.RecordFact(payment, "payment.refunded.v1", settled.OccurredAt);
        }
        else if (settled.State == ProviderPaymentState.Voided)
        {
            if (payment.RecordVoid(settled.OccurredAt)) store.RecordFact(payment, "payment.voided.v1", settled.OccurredAt);
        }
        else throw new OperationConflictException("Payment settlement is incomplete.");
        store.RecordConsumed(messageId, consumer, clock.UtcNow);
        await store.SaveAsync(cancellationToken);
    }
}
