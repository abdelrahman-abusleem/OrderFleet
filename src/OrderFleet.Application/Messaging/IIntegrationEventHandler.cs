namespace OrderFleet.Application.Messaging;

public interface IIntegrationEventHandler
{
    Task HandleAsync(IntegrationEvent message, CancellationToken cancellationToken);
}
