namespace OrderFleet.Infrastructure.Dispatch;

public sealed class DispatchJob
{
    public Guid OrderId { get; private set; }
    public int Attempts { get; private set; }
    public int AssignmentCount { get; private set; }
    public string Status { get; private set; } = "Pending";
    public DateTimeOffset NextAttemptAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public Guid? AssignmentId { get; private set; }
    private DispatchJob() { }
    internal DispatchJob(Guid orderId, DateTimeOffset now) { OrderId = orderId; NextAttemptAtUtc = now; }
    internal void Restart(DateTimeOffset now)
    {
        Attempts = 0; Status = "Pending"; NextAttemptAtUtc = now; CompletedAtUtc = null; AssignmentId = null;
    }
    internal void Finish(DateTimeOffset now) { Status = "Completed"; CompletedAtUtc = now; }
    internal void RestartForOperator(DateTimeOffset now) { Restart(now); AssignmentCount = 0; }
    internal void Exhaust(DateTimeOffset now) { Status = "Exhausted"; CompletedAtUtc = now; }
    internal void Record(string outcome, DateTimeOffset now, int maxAttempts, Guid? assignmentId = null)
    {
        Attempts++;
        if (outcome == "Assigned")
        {
            AssignmentCount++;
            Status = "Assigned"; CompletedAtUtc = now; AssignmentId = assignmentId; return;
        }
        if (Attempts >= maxAttempts) { Status = "Exhausted"; CompletedAtUtc = now; return; }
        var baseSeconds = Math.Min(300, Math.Pow(2, Math.Min(Attempts, 8)));
        var jitter = 1 + OrderId.ToByteArray()[0] / 255d * 0.2;
        NextAttemptAtUtc = now.AddSeconds(baseSeconds * jitter);
    }
}

public sealed class DispatchAttempt
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public string Outcome { get; private set; } = "";
    public Guid? CourierId { get; private set; }
    public Guid? AssignmentId { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    private DispatchAttempt() { }
    internal DispatchAttempt(Guid orderId, string outcome, DateTimeOffset now, Guid? courierId = null, Guid? assignmentId = null)
    { Id = Guid.NewGuid(); OrderId = orderId; Outcome = outcome; OccurredAtUtc = now; CourierId = courierId; AssignmentId = assignmentId; }
}
