using OrderFleet.Domain.Orders;

namespace OrderFleet.Domain.Payments;

public enum PaymentState { Authorized = 1, Captured, Failed, Voided, Refunded, PendingReconciliation }

public sealed class Payment
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid AuthorizationId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "SAR";
    public PaymentState State { get; private set; }
    public DateTimeOffset AuthorizedAt { get; private set; }
    public DateTimeOffset? CapturedAt { get; private set; }
    public DateTimeOffset? FailedAt { get; private set; }
    public DateTimeOffset? SettledAt { get; private set; }

    private Payment() { }

    public static Payment Authorize(Guid orderId, Guid authorizationId, decimal amount, string currency, DateTimeOffset now)
    {
        if (orderId == Guid.Empty) throw new ArgumentException("Order is required.", nameof(orderId));
        if (authorizationId == Guid.Empty) throw new ArgumentException("Authorization is required.", nameof(authorizationId));
        if (amount <= 0 || decimal.Round(amount, 2) != amount) throw new ArgumentOutOfRangeException(nameof(amount));
        if (currency != "SAR") throw new ArgumentException("Only SAR is supported.", nameof(currency));
        return new()
        {
            Id = Guid.NewGuid(), OrderId = orderId, AuthorizationId = authorizationId,
            Amount = amount, Currency = currency, State = PaymentState.Authorized, AuthorizedAt = now
        };
    }

    public bool RecordCapture(DateTimeOffset now)
    {
        if (State == PaymentState.Captured) return false;
        if (State is not (PaymentState.Authorized or PaymentState.Failed or PaymentState.PendingReconciliation)) throw new DomainException("Payment cannot be captured.");
        State = PaymentState.Captured;
        CapturedAt = now;
        return true;
    }

    public bool RecordCaptureFailure(DateTimeOffset now)
    {
        if (State == PaymentState.Failed) return false;
        if (State != PaymentState.Authorized) throw new DomainException("Payment cannot record capture failure.");
        State = PaymentState.Failed;
        FailedAt = now;
        return true;
    }

    public bool RecordUncertainCapture()
    {
        if (State == PaymentState.PendingReconciliation) return false;
        if (State is not (PaymentState.Authorized or PaymentState.Failed)) throw new DomainException("Payment cannot await reconciliation.");
        State = PaymentState.PendingReconciliation;
        return true;
    }

    public bool RecordVoid(DateTimeOffset now)
    {
        if (State == PaymentState.Voided) return false;
        if (State is not (PaymentState.Authorized or PaymentState.Failed or PaymentState.PendingReconciliation)) throw new DomainException("Payment cannot be voided.");
        State = PaymentState.Voided;
        SettledAt = now;
        return true;
    }

    public bool RecordRefund(DateTimeOffset now)
    {
        if (State == PaymentState.Refunded) return false;
        if (State != PaymentState.Captured) throw new DomainException("Payment cannot be refunded.");
        State = PaymentState.Refunded;
        SettledAt = now;
        return true;
    }
}
