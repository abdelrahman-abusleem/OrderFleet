using OrderFleet.Application.Messaging;
using RabbitMQ.Client;

namespace OrderFleet.Infrastructure.Messaging;

public sealed class RabbitDeliveryProcessor
{
    public async Task ProcessAsync(IChannel channel, string queue, ulong deliveryTag, IReadOnlyBasicProperties properties,
        ReadOnlyMemory<byte> body, IIntegrationEventHandler handler, CancellationToken ct)
    {
        string? failure = null;
        var permanent = false;
        try
        {
            var message = EventEnvelopeCodec.Decode(body, properties.MessageId, properties.Type);
            await handler.HandleAsync(message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            permanent = error is InvalidDataException or System.Text.Json.JsonException;
            failure = permanent ? "InvalidEnvelope" : "ProcessingFailed";
        }
        if (failure is not null)
        {
            var attempt = ReadAttempt(properties.Headers);
            var target = permanent || attempt >= RabbitTopology.RetryDelaysMilliseconds.Length
                ? RabbitTopology.Dead(queue) : RabbitTopology.Retry(queue, attempt + 1);
            var retry = new BasicProperties
            {
                Persistent = true, ContentType = properties.ContentType, MessageId = properties.MessageId,
                Type = properties.Type, CorrelationId = properties.CorrelationId,
                Headers = new Dictionary<string, object?> { ["x-retry-attempt"] = attempt + 1, ["x-failure-code"] = failure }
            };
            // Confirm the durable retry/dead-letter copy before ACKing the original.
            // Lost ACK may duplicate this copy; Inbox still fences business effects.
            await channel.BasicPublishAsync("", target, mandatory: true, retry, body, ct);
        }
        await channel.BasicAckAsync(deliveryTag, multiple: false, ct);
    }

    private static int ReadAttempt(IDictionary<string, object?>? headers)
    {
        if (headers is null || !headers.TryGetValue("x-retry-attempt", out var value)) return 0;
        return value is int attempt && attempt is >= 0 and <= 4 ? attempt : 4;
    }
}
