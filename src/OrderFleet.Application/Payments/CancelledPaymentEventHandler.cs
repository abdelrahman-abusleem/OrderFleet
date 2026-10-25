using OrderFleet.Application.Messaging;

namespace OrderFleet.Application.Payments;

public sealed class CancelledPaymentEventHandler(SettleCancelledPayment settle) : IIntegrationEventHandler
{
    public Task HandleAsync(IntegrationEvent message, CancellationToken cancellationToken)
    {
        if (message.Type != "order.cancelled.v1" || message.Version != 1) throw new InvalidDataException("Unsupported cancellation event.");
        return settle.ExecuteAsync(message.MessageId, message.AggregateId, cancellationToken);
    }
}
