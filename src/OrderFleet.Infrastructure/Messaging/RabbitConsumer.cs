using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrderFleet.Application.Payments;
using OrderFleet.Application.Dispatch;
using OrderFleet.Application.Messaging;
using OrderFleet.Application.Realtime;
using RabbitMQ.Client.Events;
using RabbitMQ.Client;

namespace OrderFleet.Infrastructure.Messaging;

public sealed class RabbitConsumer(IOptions<RabbitOptions> options, IServiceScopeFactory scopes, RabbitDeliveryProcessor processor)
{
    public Task RunPaymentsAsync(CancellationToken ct) => RunAsync(RabbitTopology.Payments(options.Value.Prefix),
        sp => sp.GetRequiredService<CancelledPaymentEventHandler>(), ct);

    public Task RunDispatchAsync(CancellationToken ct) => RunAsync(RabbitTopology.Dispatch(options.Value.Prefix),
        sp => sp.GetRequiredService<DispatchEventHandler>(), ct);

    public Task RunRealtimeAsync(CancellationToken ct) => RunAsync(RabbitTopology.Realtime(options.Value.Prefix),
        sp => sp.GetRequiredService<RealtimeEventHandler>(), ct);

    private async Task RunAsync(string queue, Func<IServiceProvider, IIntegrationEventHandler> resolve, CancellationToken ct)
    {
        await using var session = await RabbitSession.OpenAsync(options.Value, ct);
        var channel = session.Channel;
        await RabbitTopology.DeclareAsync(channel, options.Value.Prefix, ct);
        await channel.BasicQosAsync(0, 10, false, ct);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, _) => { stopped.TrySetResult(); return Task.CompletedTask; };
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var scope = scopes.CreateScope();
                var handler = resolve(scope.ServiceProvider);
                await processor.ProcessAsync(channel, queue, delivery.DeliveryTag, delivery.BasicProperties,
                    delivery.Body, handler, timeout.Token);
            }
            catch { stopped.TrySetResult(); } // Disposing the session requeues unacked deliveries.
        };
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, ct);
        await stopped.Task.WaitAsync(ct);
    }
}
