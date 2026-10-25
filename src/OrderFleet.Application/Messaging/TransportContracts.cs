namespace OrderFleet.Application.Messaging;

public sealed record OutboxLease(Guid LeaseId, IntegrationEvent Message, int Attempts);

public interface IEventPublisher
{
    Task PublishAsync(IntegrationEvent message, CancellationToken cancellationToken);
}

public interface IOutboxStore
{
    Task<IReadOnlyList<OutboxLease>> ClaimAsync(int count, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(OutboxLease lease, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> FailAsync(OutboxLease lease, DateTimeOffset now, string errorCode, int maxAttempts, CancellationToken cancellationToken);
}
