using RabbitMQ.Client;

namespace OrderFleet.Infrastructure.Messaging;

public sealed class RabbitSession(IConnection connection, IChannel channel) : IAsyncDisposable
{
    public IChannel Channel { get; } = channel;

    public static async Task<RabbitSession> OpenAsync(RabbitOptions options, CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(options.Uri),
            AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(options.PublishTimeoutSeconds),
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
            ConsumerDispatchConcurrency = 1
        };
        var connection = await factory.CreateConnectionAsync("OrderFleet", ct);
        try
        {
            var channel = await connection.CreateChannelAsync(new CreateChannelOptions(
                publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
            return new(connection, channel);
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Channel.DisposeAsync(); }
        finally { await connection.DisposeAsync(); }
    }
}
