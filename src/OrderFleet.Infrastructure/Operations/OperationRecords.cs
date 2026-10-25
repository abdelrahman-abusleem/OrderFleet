namespace OrderFleet.Infrastructure.Operations;

public sealed class OperationRequest
{
    public Guid ActorId { get; private set; }
    public string Operation { get; private set; } = "";
    public string Key { get; private set; } = "";
    public Guid TargetId { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    private OperationRequest() { }
    internal OperationRequest(Guid actorId, string operation, string key, Guid targetId, DateTimeOffset now)
    { ActorId = actorId; Operation = operation; Key = key; TargetId = targetId; OccurredAtUtc = now; }
}

public sealed class OperationAudit
{
    public Guid Id { get; private set; }
    public Guid ActorId { get; private set; }
    public string Action { get; private set; } = "";
    public Guid TargetId { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public int? PreviousAttempts { get; private set; }
    public string? PreviousOutcome { get; private set; }
    private OperationAudit() { }
    internal OperationAudit(Guid actorId, string action, Guid targetId, DateTimeOffset now, int? previousAttempts, string? previousOutcome)
    { Id = Guid.NewGuid(); ActorId = actorId; Action = action; TargetId = targetId; OccurredAtUtc = now; PreviousAttempts = previousAttempts; PreviousOutcome = previousOutcome; }
}
