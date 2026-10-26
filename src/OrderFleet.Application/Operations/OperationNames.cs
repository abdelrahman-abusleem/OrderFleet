namespace OrderFleet.Application.Operations;

public static class OperationNames
{
    public const string RetryOutbox = "RetryOutbox";
    public const string RetryDispatch = "RetryDispatch";
    public const string RetryRecoveryPrefix = "RetryRecovery:";
}
