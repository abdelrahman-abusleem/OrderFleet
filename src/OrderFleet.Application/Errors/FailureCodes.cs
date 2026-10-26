namespace OrderFleet.Application.Errors;

public static class FailureCodes
{
    public const string BrokerUnavailable = "BrokerUnavailable";
    public const string InvalidEnvelope = "InvalidEnvelope";
    public const string ProcessingFailed = "ProcessingFailed";
    public const string DependencyOrConflict = "DependencyOrConflict";
    public const string InvariantViolation = "InvariantViolation";
    public const string ConsumerTerminated = "ConsumerTerminated";
}
