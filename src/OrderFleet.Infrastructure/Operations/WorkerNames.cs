namespace OrderFleet.Infrastructure.Operations;

public static class WorkerNames
{
    public const string Outbox = "outbox";
    public const string Payments = "payments";
    public const string Dispatch = "dispatch";
    public const string Realtime = "realtime";
    public const string Financial = "financial";
    public const string Recovery = "recovery";
    public const string Operations = "operations";
}
