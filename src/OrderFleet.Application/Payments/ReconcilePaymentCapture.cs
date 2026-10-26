using OrderFleet.Application.Clock;
using OrderFleet.Application.Messaging;
using OrderFleet.Domain.Payments;

namespace OrderFleet.Application.Payments;

public sealed class ReconcilePaymentCapture(IPaymentStore store, IPaymentProvider provider, IClock clock)
{
    public async Task ExecuteAsync(Guid orderId, CancellationToken ct)
    {
        var payment = await store.FindAsync(orderId, ct);
        if (payment?.State != PaymentState.PendingReconciliation) return;
        // Read-only provider reconciliation must not initiate any financial effect.
        var actual = await provider.FindCaptureAsync(payment.AuthorizationId, ct);
        if (actual is null) return;
        if (actual.Succeeded)
        {
            if (payment.RecordCapture(actual.OccurredAt)) store.RecordFact(payment, IntegrationEventTypes.PaymentCaptured, actual.OccurredAt);
        }
        else
        {
            payment.RecordReconciledDecline(clock.UtcNow);
            store.RecordFact(payment, IntegrationEventTypes.PaymentFailed, clock.UtcNow);
        }
        store.RecordAttempt(payment, actual.Succeeded ? CaptureClassification.Succeeded : CaptureClassification.PermanentDecline, clock.UtcNow);
        await store.SaveAsync(ct);
    }
}
