namespace OrderFleet.Infrastructure.Recovery;

public sealed class RecoveryWorkFailure
{
    public string Operation { get; private set; } = "";
    public Guid ResourceId { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset NextAttemptAtUtc { get; private set; }
    public DateTimeOffset? DeadLetteredAtUtc { get; private set; }
    public DateTimeOffset? ResolvedAtUtc { get; private set; }
    public string? LastErrorCode { get; private set; }
    private RecoveryWorkFailure() { }
    internal void Record(DateTimeOffset now, string code, int maxAttempts)
    {
        Attempts = ResolvedAtUtc.HasValue ? 1 : Attempts + 1;
        ResolvedAtUtc = null;
        LastErrorCode = code;
        NextAttemptAtUtc = now.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(Attempts, 9))));
        DeadLetteredAtUtc = Attempts >= maxAttempts ? now : null;
    }
    internal void Retry(DateTimeOffset now) { Attempts = 0; DeadLetteredAtUtc = null; NextAttemptAtUtc = now; ResolvedAtUtc = null; }
}
