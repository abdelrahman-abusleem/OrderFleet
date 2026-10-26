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
        "order.created.v1", "order.accepted.v1", "order.courier-assigned.v1", "order.assignment-released.v1",
        "order.picked-up.v1", "order.delivered.v1", "order.cancelled.v1", "payment.authorized.v1",
        "payment.captured.v1", "payment.failed.v1", "payment.capture-uncertain.v1", "payment.voided.v1", "payment.refunded.v1",
        "courier.location-updated.v1"
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
            || (message.Type.StartsWith("order.", StringComparison.Ordinal) && message.AggregateSequence is not > 0))
            throw new InvalidDataException("Invalid event envelope.");
        using var payload = JsonDocument.Parse(message.PayloadJson, new JsonDocumentOptions { MaxDepth = 8 });
        var identity = message.Type == "courier.location-updated.v1" ? "courierId" : "orderId";
        if (payload.RootElement.ValueKind != JsonValueKind.Object
            || !payload.RootElement.TryGetProperty(identity, out var order) || order.ValueKind != JsonValueKind.String
            || !order.TryGetGuid(out var id) || id != message.AggregateId
            || payload.RootElement.TryGetProperty("latitude", out _) || payload.RootElement.TryGetProperty("longitude", out _))
            throw new InvalidDataException("Invalid event payload.");
    }
}
