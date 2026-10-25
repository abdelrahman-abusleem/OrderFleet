using RabbitMQ.Client;

namespace OrderFleet.Infrastructure.Messaging;

public static class RabbitTopology
{
    public static string Events(string prefix) => $"{prefix}.events.v1";
    public static string Payments(string prefix) => $"{prefix}.payments.cancelled.v1";
    public static string Dispatch(string prefix) => $"{prefix}.dispatch.v1";
    public static string Realtime(string prefix) => $"{prefix}.realtime.v1";
    public static string Financial(string prefix) => $"{prefix}.financial.v1";
    public static string Dead(string queue) => $"{queue}.dead";
    public static string Retry(string queue, int attempt) => $"{queue}.retry.{attempt}";
    public static readonly int[] RetryDelaysMilliseconds = [1000, 5000, 30000, 120000];

    public static async Task DeclareAsync(IChannel channel, string prefix, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Events(prefix), ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        await DeclareQueueAsync(channel, Payments(prefix), ct);
        await DeclareQueueAsync(channel, Dispatch(prefix), ct);
        await DeclareQueueAsync(channel, Realtime(prefix), ct);
        await DeclareQueueAsync(channel, Financial(prefix), ct);
        await channel.QueueBindAsync(Payments(prefix), Events(prefix), "order.cancelled.v1", cancellationToken: ct);
        await channel.QueueBindAsync(Dispatch(prefix), Events(prefix), "order.accepted.v1", cancellationToken: ct);
        await channel.QueueBindAsync(Dispatch(prefix), Events(prefix), "order.assignment-released.v1", cancellationToken: ct);
        await channel.QueueBindAsync(Realtime(prefix), Events(prefix), "order.#", cancellationToken: ct);
        await channel.QueueBindAsync(Realtime(prefix), Events(prefix), "courier.location-updated.v1", cancellationToken: ct);
        await channel.QueueBindAsync(Financial(prefix), Events(prefix), "payment.#", cancellationToken: ct);
    }

    private static async Task DeclareQueueAsync(IChannel channel, string queue, CancellationToken ct)
    {
        await channel.QueueDeclareAsync(Dead(queue), durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: ct);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum", ["x-delivery-limit"] = 20,
                ["x-dead-letter-exchange"] = "", ["x-dead-letter-routing-key"] = Dead(queue),
                ["x-dead-letter-strategy"] = "at-least-once", ["x-overflow"] = "reject-publish"
            }, cancellationToken: ct);
        for (var index = 0; index < RetryDelaysMilliseconds.Length; index++)
            await channel.QueueDeclareAsync(Retry(queue, index + 1), durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-message-ttl"] = RetryDelaysMilliseconds[index],
                    ["x-dead-letter-exchange"] = "", ["x-dead-letter-routing-key"] = queue,
                    ["x-queue-type"] = "quorum", ["x-dead-letter-strategy"] = "at-least-once", ["x-overflow"] = "reject-publish"
                }, cancellationToken: ct);
    }
}
