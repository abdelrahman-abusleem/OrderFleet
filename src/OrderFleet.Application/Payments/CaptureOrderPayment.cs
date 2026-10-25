using OrderFleet.Application.Clock;
using OrderFleet.Application.Errors;
using OrderFleet.Domain.Orders;
using OrderFleet.Domain.Payments;

namespace OrderFleet.Application.Payments;

// Captures against a stable provider operation; the caller commits successful capture
// facts with acceptance, while failures are persisted before returning their outcome.
public sealed class CaptureOrderPayment(IPaymentStore payments, IPaymentProvider provider, IClock clock)
{
    public async Task ExecuteAsync(Order order, CancellationToken cancellationToken)
    {
        var payment = await payments.FindAsync(order.Id, cancellationToken)
            ?? throw new OperationConflictException("Payment authorization is missing.");
        if (payment.Amount != order.TotalAmount || payment.Currency != order.Currency)
            throw new OperationConflictException("Payment does not match the order snapshot.");
        ProviderCapture capture;
        try { capture = await provider.CaptureAsync(payment.AuthorizationId, cancellationToken); }
        catch (DependencyUnavailableException)
        {
            await RecordCaptureFailureAsync(payment, CaptureClassification.TransientFailure, cancellationToken);
            throw;
        }
        catch (ProviderOutcomeUnknownException)
        {
            var observedAt = clock.UtcNow;
            if (payment.RecordUncertainCapture()) payments.RecordFact(payment, "payment.capture-uncertain.v1", observedAt);
            payments.RecordAttempt(payment, CaptureClassification.Unknown, observedAt);
            await payments.SaveAsync(cancellationToken);
            throw;
        }
        if (!capture.Succeeded)
        {
            await RecordCaptureFailureAsync(payment, CaptureClassification.PermanentDecline, cancellationToken);
            throw new OperationConflictException("Payment capture was declined.");
        }
        if (payment.RecordCapture(capture.OccurredAt)) payments.RecordFact(payment, "payment.captured.v1", capture.OccurredAt);
        payments.RecordAttempt(payment, CaptureClassification.Succeeded, capture.OccurredAt);
    }

    private async Task RecordCaptureFailureAsync(Payment payment, CaptureClassification classification, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (payment.RecordCaptureFailure(now)) payments.RecordFact(payment, "payment.failed.v1", now);
        payments.RecordAttempt(payment, classification, now);
        await payments.SaveAsync(cancellationToken);
    }
}
