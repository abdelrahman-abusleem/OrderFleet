using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrderFleet.Application.Payments;
using RabbitMQ.Client.Events;
using RabbitMQ.Client;

namespace OrderFleet.Infrastructure.Messaging;

public sealed class RabbitConsumer(IOptions<RabbitOptions> options, IServiceScopeFactory scopes, RabbitDeliveryProcessor processor)
{
    public async Task RunPaymentsAsync(CancellationToken ct)
    {
        await using var session = await RabbitSession.OpenAsync(options.Value, ct);
        var channel = session.Channel;
        await RabbitTopology.DeclareAsync(channel, options.Value.Prefix, ct);
        await channel.BasicQosAsync(0, 10, false, ct);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, _) => { stopped.TrySetResult(); return Task.CompletedTask; };
        var consumer = new AsyncEventingBasicConsumer(channel);
        var queue = RabbitTopology.Payments(options.Value.Prefix);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var scope = scopes.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<CancelledPaymentEventHandler>();
                await processor.ProcessAsync(channel, queue, delivery.DeliveryTag, delivery.BasicProperties,
                    delivery.Body, handler, timeout.Token);
            }
            catch { stopped.TrySetResult(); } // Disposing the session requeues unacked deliveries.
        };
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, ct);
        await stopped.Task.WaitAsync(ct);
    }
}
