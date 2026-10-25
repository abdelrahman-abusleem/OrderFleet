using OrderFleet.Application.Payments;
using OrderFleet.Domain.Payments;

namespace OrderFleet.Infrastructure.Payments;

public sealed class PaymentCaptureAttempt
{
    public Guid Id { get; private set; }
    public Guid PaymentId { get; private set; }
    public string IdempotencyKey { get; private set; } = "";
    public CaptureClassification Classification { get; private set; }
    public bool IsRetryable { get; private set; }
    public bool RequiresReconciliation { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    private PaymentCaptureAttempt() { }
    public PaymentCaptureAttempt(Payment payment, CaptureClassification classification, DateTimeOffset now)
    {
        Id = Guid.NewGuid(); PaymentId = payment.Id; IdempotencyKey = $"capture:{payment.AuthorizationId:N}";
        Classification = classification;
        IsRetryable = classification is CaptureClassification.TransientFailure or CaptureClassification.Unknown;
        RequiresReconciliation = classification == CaptureClassification.Unknown;
        OccurredAtUtc = now.ToUniversalTime();
    }
}
