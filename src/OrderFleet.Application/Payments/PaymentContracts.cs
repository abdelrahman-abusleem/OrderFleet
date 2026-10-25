using OrderFleet.Domain.Payments;

namespace OrderFleet.Application.Payments;

public enum ProviderPaymentState { Authorized = 1, Declined, Captured, Voided, Refunded }
public sealed record ProviderAuthorization(Guid Id, ProviderPaymentState State, decimal Amount, string Currency, DateTimeOffset CreatedAt);
public sealed record ProviderCapture(bool Succeeded, DateTimeOffset OccurredAt);
public sealed record ProviderSettlement(ProviderPaymentState State, DateTimeOffset OccurredAt, DateTimeOffset? CapturedAt);
public enum CaptureClassification { Succeeded = 1, PermanentDecline, TransientFailure, Unknown }

public interface IPaymentProvider
{
    Task<ProviderAuthorization> AuthorizeAsync(Guid actorId, string key, string fingerprint, decimal amount, string currency, CancellationToken cancellationToken);
    Task<ProviderCapture> CaptureAsync(Guid authorizationId, CancellationToken cancellationToken);
    Task<ProviderSettlement> SettleAsync(Guid authorizationId, CancellationToken cancellationToken);
    Task ReleaseUnusedAuthorizationAsync(Guid authorizationId, CancellationToken cancellationToken);
}

public interface IPaymentStore
{
    Task<Payment?> FindAsync(Guid orderId, CancellationToken cancellationToken);
    Task<bool> IsCancelledAsync(Guid orderId, CancellationToken cancellationToken);
    Task<bool> IsConsumedAsync(Guid messageId, string consumer, CancellationToken cancellationToken);
    void RecordFact(Payment payment, string type, DateTimeOffset now);
    void RecordConsumed(Guid messageId, string consumer, DateTimeOffset now);
    void RecordAttempt(Payment payment, CaptureClassification classification, DateTimeOffset now);
    Task SaveAsync(CancellationToken cancellationToken);
}
