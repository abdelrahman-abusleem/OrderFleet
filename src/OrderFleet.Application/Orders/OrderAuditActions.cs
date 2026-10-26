namespace OrderFleet.Application.Orders;

public static class OrderAuditActions
{
    public const string Created = "OrderCreated";
    public const string Accepted = "OrderAccepted";
    public const string Rejected = "OrderRejected";
    public const string Cancelled = "OrderCancelled";
    public const string CourierDispatched = "CourierDispatched";
    public const string AcceptanceTimedOut = "AcceptanceTimedOut";
    public const string PickupReservationExpired = "PickupReservationExpired";
    public const string CourierActionPrefix = "Courier";
}
