using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using OrderFleet.Application.Messaging;

namespace OrderFleet.Infrastructure.Messaging;

public static class EventEnvelopeCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    private static readonly HashSet<string> Types =
    [
        IntegrationEventTypes.OrderCreated, IntegrationEventTypes.OrderAccepted, IntegrationEventTypes.CourierAssigned, IntegrationEventTypes.AssignmentReleased,
        IntegrationEventTypes.OrderPickedUp, IntegrationEventTypes.OrderDelivered, IntegrationEventTypes.OrderCancelled, IntegrationEventTypes.PaymentAuthorized,
        IntegrationEventTypes.PaymentCaptured, IntegrationEventTypes.PaymentFailed, IntegrationEventTypes.PaymentCaptureUncertain, IntegrationEventTypes.PaymentVoided, IntegrationEventTypes.PaymentRefunded,
        IntegrationEventTypes.CourierLocationUpdated
    ];

    public static byte[] Encode(IntegrationEvent message)
    {
        Validate(message);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length > 65536) throw new InvalidDataException("Envelope exceeds size limit.");
        return bytes;
    }

    public static IntegrationEvent Decode(ReadOnlyMemory<byte> body, string? messageId, string? type)
    {
        if (body.Length > 65536) throw new InvalidDataException("Envelope exceeds size limit.");
        var message = JsonSerializer.Deserialize<IntegrationEvent>(body.Span, Json) ?? throw new InvalidDataException("Missing envelope.");
        Validate(message);
        if (messageId != message.MessageId.ToString("D") || type != message.Type) throw new InvalidDataException("Envelope identity mismatch.");
        return message;
    }

    private static void Validate(IntegrationEvent message)
    {
        if (message.MessageId == Guid.Empty || message.AggregateId == Guid.Empty || message.Version != 1
            || !Types.Contains(message.Type) || message.OccurredAtUtc == default || message.OccurredAtUtc.Offset != TimeSpan.Zero
            || message.CorrelationId?.Length > 64 || message.PayloadJson is null || message.PayloadJson.Length > 60000
            || (message.TraceParent is not null && (message.TraceParent.Length > 55 || !ActivityContext.TryParse(message.TraceParent, null, out _)))
            || (message.Type.StartsWith(IntegrationEventTypes.OrderPrefix, StringComparison.Ordinal) && message.AggregateSequence is not > 0))
            throw new InvalidDataException("Invalid event envelope.");
        using var payload = JsonDocument.Parse(message.PayloadJson, new JsonDocumentOptions { MaxDepth = 8 });
        var identity = message.Type == IntegrationEventTypes.CourierLocationUpdated ? "courierId" : "orderId";
        if (payload.RootElement.ValueKind != JsonValueKind.Object
            || !payload.RootElement.TryGetProperty(identity, out var order) || order.ValueKind != JsonValueKind.String
            || !order.TryGetGuid(out var id) || id != message.AggregateId
            || payload.RootElement.TryGetProperty("latitude", out _) || payload.RootElement.TryGetProperty("longitude", out _))
            throw new InvalidDataException("Invalid event payload.");
    }
}
