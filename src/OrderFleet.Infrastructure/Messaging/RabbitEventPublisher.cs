using Microsoft.Extensions.Options;
using OrderFleet.Application.Messaging;
using RabbitMQ.Client;
using System.Diagnostics;
using System.Text;
using OrderFleet.Application.Diagnostics;

namespace OrderFleet.Infrastructure.Messaging;

public sealed class RabbitEventPublisher(IOptions<RabbitOptions> options) : IEventPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private RabbitSession? session;

    public async Task PublishAsync(IntegrationEvent message, CancellationToken cancellationToken)
    {
        var bytes = EventEnvelopeCodec.Encode(message);
        ActivityContext.TryParse(message.TraceParent, null, isRemote: true, out var parent);
        using var activity = OrderFleetDiagnostics.Activities.StartActivity("messaging.publish", ActivityKind.Producer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.operation.type", "publish");
        activity?.SetTag("messaging.message.id", message.MessageId.ToString("D"));
        await gate.WaitAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.PublishTimeoutSeconds));
        try
        {
            if (session is null)
            {
                session = await RabbitSession.OpenAsync(options.Value, timeout.Token);
                await RabbitTopology.DeclareAsync(session.Channel, options.Value.Prefix, timeout.Token);
            }
            var properties = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                MessageId = message.MessageId.ToString("D"),
                Type = message.Type,
                CorrelationId = message.CorrelationId,
                Headers = new Dictionary<string, object?> { ["traceparent"] = Encoding.ASCII.GetBytes(activity?.Id ?? message.TraceParent ?? "") }
            };


            await session.Channel.BasicPublishAsync(RabbitTopology.Events(options.Value.Prefix), message.Type,
                mandatory: true, properties, bytes, timeout.Token);
        }
        catch
        {
            if (session is not null) { await session.DisposeAsync(); session = null; }
            throw;
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (session is not null) await session.DisposeAsync(); }
        finally { gate.Release(); gate.Dispose(); }
    }
}
