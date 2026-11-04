namespace OrderFleet.Infrastructure.Dispatch;


public static class DispatchJobStatuses
{
    public const string Pending = "Pending";
    public const string Assigned = "Assigned";
    public const string Completed = "Completed";
    public const string Exhausted = "Exhausted";
}

public static class DispatchAttemptOutcomes
{
    public const string NoCourier = "NoCourier";
    public const string Assigned = "Assigned";
    public const string AssignmentLimit = "AssignmentLimit";
}
