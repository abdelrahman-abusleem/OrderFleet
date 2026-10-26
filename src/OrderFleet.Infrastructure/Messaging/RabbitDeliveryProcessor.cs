using OrderFleet.Application.Messaging;
using OrderFleet.Application.Errors;
using RabbitMQ.Client;
using System.Diagnostics;
using System.Text;
using OrderFleet.Application.Diagnostics;
using Microsoft.Extensions.Logging;
using OrderFleet.Infrastructure.Operations;

namespace OrderFleet.Infrastructure.Messaging;

public sealed class RabbitDeliveryProcessor(ILogger<RabbitDeliveryProcessor>? logger = null)
{
    // Preserve the existing direct-call contract; hosted consumers pass their
    // known worker name explicitly through the overload below.
    public Task ProcessAsync(IChannel channel, string queue, ulong deliveryTag, IReadOnlyBasicProperties properties,
        ReadOnlyMemory<byte> body, IIntegrationEventHandler handler, CancellationToken ct) =>
        ProcessAsync(channel, queue, LegacyWorkerName(queue), deliveryTag, properties, body, handler, ct);

    public async Task ProcessAsync(IChannel channel, string queue, string consumerName, ulong deliveryTag, IReadOnlyBasicProperties properties,
        ReadOnlyMemory<byte> body, IIntegrationEventHandler handler, CancellationToken ct)
    {
        var traceParent = properties.Headers?.TryGetValue("traceparent", out var trace) == true
            && trace is byte[] { Length: <= 55 } bytes ? Encoding.ASCII.GetString(bytes) : null;
        ActivityContext.TryParse(traceParent, null, isRemote: true, out var parent);
        using var activity = OrderFleetDiagnostics.Activities.StartActivity("messaging.consume", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.operation.type", "process");
        var failure = await TryHandleAsync(properties, body, handler, activity, ct);
        if (failure is not null)
            await RouteFailureAsync(channel, queue, consumerName, properties, body, failure, activity?.Id ?? traceParent, ct);
        // Handler commit or confirmed durable retry copy always precedes ACK.
        await channel.BasicAckAsync(deliveryTag, multiple: false, ct);
    }

    private static async Task<ProcessingFailure?> TryHandleAsync(IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body,
        IIntegrationEventHandler handler, Activity? activity, CancellationToken ct)
    {
        try
        {
            var message = EventEnvelopeCodec.Decode(body, properties.MessageId, properties.Type);
            activity?.SetTag("messaging.message.id", message.MessageId.ToString("D"));
            await handler.HandleAsync(message, ct);
            OrderFleetDiagnostics.ConsumerProcessed.Add(1);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var permanent = error is InvalidDataException or System.Text.Json.JsonException;
            var code = permanent ? FailureCodes.InvalidEnvelope : FailureCodes.ProcessingFailed;
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("error.type", code);
            return new(code, permanent);
        }
    }

    private async Task RouteFailureAsync(IChannel channel, string queue, string consumerName, IReadOnlyBasicProperties properties,
        ReadOnlyMemory<byte> body, ProcessingFailure failure, string? traceParent, CancellationToken ct)
    {
        var attempt = ReadAttempt(properties.Headers);
        var target = failure.Permanent || attempt >= RabbitTopology.RetryDelaysMilliseconds.Length
            ? RabbitTopology.Dead(queue) : RabbitTopology.Retry(queue, attempt + 1);
        if (target == RabbitTopology.Dead(queue)) OrderFleetDiagnostics.ConsumerDeadLetters.Add(1);
        else OrderFleetDiagnostics.ConsumerRetries.Add(1);
        var messageId = Guid.TryParse(properties.MessageId, out var parsed) ? parsed : Guid.Empty;
        logger?.Log(target == RabbitTopology.Dead(queue) ? LogLevel.Error : LogLevel.Warning,
            "Consumer {Consumer} failed {MessageId} {FailureCode} attempt {Attempt}", consumerName, messageId, failure.Code, attempt + 1);
        var retry = new BasicProperties
        {
            Persistent = true,
            ContentType = properties.ContentType,
            MessageId = properties.MessageId,
            Type = properties.Type,
            CorrelationId = properties.CorrelationId,
            Headers = new Dictionary<string, object?>
            {
                ["x-retry-attempt"] = attempt + 1,
                ["x-failure-code"] = failure.Code,
                ["traceparent"] = Encoding.ASCII.GetBytes(traceParent ?? "")
            }
        };
        // Lost ACK may duplicate this confirmed copy; handlers fence via Inbox.
        await channel.BasicPublishAsync("", target, mandatory: true, retry, body, ct);
    }

    private static string LegacyWorkerName(string queue) => queue.EndsWith(".financial.v1", StringComparison.Ordinal) ? WorkerNames.Financial
        : queue.EndsWith(".dispatch.v1", StringComparison.Ordinal) ? WorkerNames.Dispatch
        : queue.EndsWith(".realtime.v1", StringComparison.Ordinal) ? WorkerNames.Realtime : WorkerNames.Payments;

    private sealed record ProcessingFailure(string Code, bool Permanent);

    private static int ReadAttempt(IDictionary<string, object?>? headers)
    {
        if (headers is null || !headers.TryGetValue("x-retry-attempt", out var value)) return 0;
        return value is int attempt && attempt is >= 0 and <= 4 ? attempt : 4;
    }
}
