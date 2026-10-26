namespace OrderFleet.Application.Messaging;

public static class ConsumerNames
{
    public const string Dispatch = "dispatch.v1";
    public const string Realtime = "realtime.v1";
    public const string CancelledPayments = "payments.cancelled.v1";
    public const string FinancialArchive = "financial.archive.v1";
}
