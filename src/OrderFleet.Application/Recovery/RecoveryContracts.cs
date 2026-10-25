namespace OrderFleet.Application.Recovery;

public sealed record ExpiredAssignment(Guid OrderId, Guid AssignmentId);
public sealed record CancelledPayment(Guid OrderId, Guid MessageId);
public sealed record RecoveryPolicy(int BatchSize, TimeSpan OrphanGrace);

public interface IRecoveryStore
{
    Task<IReadOnlyList<Guid>> ExpiredOrdersAsync(DateTimeOffset now, int count, CancellationToken ct);
    Task<bool> ExpireOrderAsync(Guid orderId, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<ExpiredAssignment>> ExpiredAssignmentsAsync(DateTimeOffset now, int count, CancellationToken ct);
    Task<bool> ReleaseExpiredAssignmentAsync(Guid orderId, Guid assignmentId, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<Guid>> DueDispatchAsync(DateTimeOffset now, int count, CancellationToken ct);
    Task<IReadOnlyList<CancelledPayment>> UnsettledCancellationsAsync(int count, CancellationToken ct);
    Task ReconcileCancellationAsync(CancelledPayment payment, CancellationToken ct);
    Task<IReadOnlyList<Guid>> UncertainPaymentsAsync(int count, CancellationToken ct);
    Task ReconcileCaptureAsync(Guid orderId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> OrphanAuthorizationsAsync(DateTimeOffset cutoff, int count, CancellationToken ct);
}
