using System.Diagnostics;
using System.Text.Json;
using OrderFleet.Domain.Orders;
using OrderFleet.Domain.Payments;
using OrderFleet.Application.Diagnostics;

namespace OrderFleet.Application.Messaging;

public sealed record IntegrationEvent(Guid MessageId, string Type, int Version, DateTimeOffset OccurredAtUtc,
    Guid AggregateId, string? CorrelationId, string PayloadJson, long? AggregateSequence = null, string? TraceParent = null);

public static class IntegrationEvents
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IntegrationEvent FromOrder(OrderDomainEvent fact)
    {
        var type = fact switch
        {
            OrderCreated => "order.created.v1",
            OrderAccepted => "order.accepted.v1",
            CourierAssigned => "order.courier-assigned.v1",
            AssignmentReleased => "order.assignment-released.v1",
            OrderPickedUp => "order.picked-up.v1",
            OrderDelivered => "order.delivered.v1",
            OrderCancelled => "order.cancelled.v1",
            _ => throw new ArgumentOutOfRangeException(nameof(fact))
        };
        Guid? courier = fact switch { CourierAssigned x => x.CourierId, OrderPickedUp x => x.CourierId, OrderDelivered x => x.CourierId, _ => null };
        Guid? assignment = fact switch { CourierAssigned x => x.AssignmentId, OrderPickedUp x => x.AssignmentId, OrderDelivered x => x.AssignmentId, _ => null };
        Guid? previousCourier = fact switch { AssignmentReleased x => x.PreviousCourierId, OrderCancelled x => x.PreviousCourierId, _ => null };
        Guid? previousAssignment = fact switch { AssignmentReleased x => x.PreviousAssignmentId, OrderCancelled x => x.PreviousAssignmentId, _ => null };
        var payload = new
        {
            orderId = fact.OrderId,
            status = fact.Status.ToString(),
            fact.TotalAmount,
            courierId = courier,
            assignmentId = assignment,
            previousCourierId = previousCourier,
            previousAssignmentId = previousAssignment,
            reason = (fact as OrderCancelled)?.Reason.ToString()
        };
        return new(fact.EventId, type, 1, fact.OccurredAt.ToUniversalTime(), fact.OrderId,
            Activity.Current?.TraceId.ToString(), JsonSerializer.Serialize(payload, Json), TraceParent: OrderFleetDiagnostics.CurrentTraceParent);
    }

    public static IntegrationEvent FromPayment(Payment payment, string type, DateTimeOffset now) =>
        new(Guid.NewGuid(), type, 1, now.ToUniversalTime(), payment.OrderId, Activity.Current?.TraceId.ToString(),
            JsonSerializer.Serialize(new
            {
                paymentId = payment.Id,
                payment.OrderId,
                payment.AuthorizationId,
                payment.Amount,
                payment.Currency,
                state = payment.State.ToString()
            }, Json), TraceParent: OrderFleetDiagnostics.CurrentTraceParent);
}
