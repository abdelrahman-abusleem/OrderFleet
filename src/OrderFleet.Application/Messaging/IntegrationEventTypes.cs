namespace OrderFleet.Application.Messaging;

public static class IntegrationEventTypes
{
    public const string OrderCreated = "order.created.v1";
    public const string OrderAccepted = "order.accepted.v1";
    public const string CourierAssigned = "order.courier-assigned.v1";
    public const string AssignmentReleased = "order.assignment-released.v1";
    public const string OrderPickedUp = "order.picked-up.v1";
    public const string OrderDelivered = "order.delivered.v1";
    public const string OrderCancelled = "order.cancelled.v1";
    public const string CourierLocationUpdated = "courier.location-updated.v1";
    public const string PaymentAuthorized = "payment.authorized.v1";
    public const string PaymentCaptured = "payment.captured.v1";
    public const string PaymentFailed = "payment.failed.v1";
    public const string PaymentCaptureUncertain = "payment.capture-uncertain.v1";
    public const string PaymentVoided = "payment.voided.v1";
    public const string PaymentRefunded = "payment.refunded.v1";
    public const string OrderPrefix = "order.";
    public const string PaymentPrefix = "payment.";
}
