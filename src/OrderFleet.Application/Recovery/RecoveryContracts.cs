using System.Collections.Frozen;

namespace OrderFleet.Application.Recovery;

public sealed record ExpiredAssignment(Guid OrderId, Guid AssignmentId);
public sealed record CancelledPayment(Guid OrderId, Guid MessageId);
public sealed record RecoveryPolicy(int BatchSize, TimeSpan OrphanGrace, int MaxFailureAttempts);
public static class RecoveryWork
{
    public const string AcceptanceTimeout = "AcceptanceTimeout";
    public const string AssignmentTimeout = "AssignmentTimeout";
    public const string DispatchRetry = "DispatchRetry";
    public const string PaymentSettlement = "PaymentSettlement";
    public const string CaptureReconciliation = "CaptureReconciliation";
    public const string OrphanAuthorization = "OrphanAuthorization";
    public static IReadOnlySet<string> All { get; } = new[]
        { AcceptanceTimeout, AssignmentTimeout, DispatchRetry, PaymentSettlement, CaptureReconciliation, OrphanAuthorization }.ToFrozenSet(StringComparer.Ordinal);
}

public interface IRecoveryStore
{
    Task<IReadOnlyList<Guid>> ExpiredOrdersAsync(DateTimeOffset now, int count, CancellationToken ct);
    Task<bool> ExpireOrderAsync(Guid orderId, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<ExpiredAssignment>> ExpiredAssignmentsAsync(DateTimeOffset now, int count, CancellationToken ct);
    Task<bool> ReleaseExpiredAssignmentAsync(Guid orderId, Guid assignmentId, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<Guid>> DueDispatchAsync(DateTimeOffset now, int count, CancellationToken ct);
    Task<IReadOnlyList<CancelledPayment>> UnsettledCancellationsAsync(DateTimeOffset now, int count, CancellationToken ct);
    Task ReconcileCancellationAsync(CancelledPayment payment, CancellationToken ct);
    Task<IReadOnlyList<Guid>> UncertainPaymentsAsync(DateTimeOffset now, int count, CancellationToken ct);
    Task ReconcileCaptureAsync(Guid orderId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> OrphanAuthorizationsAsync(DateTimeOffset cutoff, DateTimeOffset now, int count, CancellationToken ct);
    Task RecordFailureAsync(string operation, Guid resourceId, DateTimeOffset now, string errorCode, int maxAttempts, CancellationToken ct);
    Task RecordResolvedAsync(string operation, Guid resourceId, DateTimeOffset now, CancellationToken ct);
    Task ResolveObsoleteFailuresAsync(DateTimeOffset now, int count, CancellationToken ct);
}
