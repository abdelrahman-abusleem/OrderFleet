namespace OrderFleet.Infrastructure.Persistence;

internal static class DatabaseConstraintNames
{
    internal const string MockAuthorizationRequest = "IX_MockPaymentAuthorizations_ActorId_Key";
    internal const string InboxIdentity = "PK_InboxMessages";
    internal const string CourierIdentity = "PK_Couriers";
    internal const string CourierPositionIdentity = "PK_CourierPositions";
    internal const string OrderRequestIdentity = "PK_OrderCreationRequests";
    internal const string PaymentAuthorization = "IX_Payments_AuthorizationId";
    internal const string OrderEventSequence = "IX_OutboxMessages_AggregateId_AggregateSequence";
}
