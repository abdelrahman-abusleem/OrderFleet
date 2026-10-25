namespace OrderFleet.Application.Operations;

public sealed record OperationsSummary(long OutboxPending, long OutboxDead, double OldestOutboxLagSeconds,
    long DispatchPending, long DispatchExhausted, long UncertainPayments, long UnsettledCancellations, long RecoveryDead);
public sealed record OutboxDiagnostic(Guid Id, string Type, DateTimeOffset OccurredAtUtc, int Attempts,
    DateTimeOffset NextAttemptAtUtc, DateTimeOffset? DeadLetteredAtUtc, string? LastErrorCode);
public sealed record RecoveryDiagnostic(string Operation, Guid ResourceId, int Attempts, DateTimeOffset NextAttemptAtUtc,
    DateTimeOffset? DeadLetteredAtUtc, string? LastErrorCode);

public interface IOperationsStore
{
    Task<bool> IsAdministratorAsync(Guid actorId, CancellationToken ct);
    Task<OperationsSummary> SummaryAsync(DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<OutboxDiagnostic>> OutboxAsync(string state, int offset, int count, CancellationToken ct);
    Task RetryOutboxAsync(Guid id, Guid actorId, string key, DateTimeOffset now, CancellationToken ct);
    Task RetryDispatchAsync(Guid orderId, Guid actorId, string key, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<RecoveryDiagnostic>> RecoveryFailuresAsync(string state, int offset, int count, CancellationToken ct);
    Task RetryRecoveryAsync(string operation, Guid resourceId, Guid actorId, string key, DateTimeOffset now, CancellationToken ct);
}
