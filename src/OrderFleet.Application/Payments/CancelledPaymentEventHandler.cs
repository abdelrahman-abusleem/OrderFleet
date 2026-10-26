using OrderFleet.Application.Messaging;

namespace OrderFleet.Application.Payments;

public sealed class CancelledPaymentEventHandler(SettleCancelledPayment settle) : IIntegrationEventHandler
{
    public Task HandleAsync(IntegrationEvent message, CancellationToken cancellationToken)
    {
        if (message.Type != IntegrationEventTypes.OrderCancelled || message.Version != 1) throw new InvalidDataException("Unsupported cancellation event.");
        return settle.ExecuteAsync(message.MessageId, message.AggregateId, cancellationToken);
    }
}
