namespace OrderFleet.Application.Operations;

public sealed record WorkerDiagnostic(string Worker, string State, bool Ready, double ProgressAgeSeconds, DateTimeOffset? LastSuccessUtc);
public sealed record QueueDiagnostic(string Consumer, string Kind, uint ReadyMessages, uint Consumers, DateTimeOffset ObservedAtUtc);
public sealed record RuntimeDiagnostic(IReadOnlyList<WorkerDiagnostic> Workers, IReadOnlyList<QueueDiagnostic> Queues);
public interface IRuntimeDiagnostics { RuntimeDiagnostic Snapshot(); }
