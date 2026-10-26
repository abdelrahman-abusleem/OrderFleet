using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrderFleet.Application.Payments;
using OrderFleet.Application.Dispatch;
using OrderFleet.Application.Messaging;
using OrderFleet.Application.Realtime;
using RabbitMQ.Client.Events;
using RabbitMQ.Client;
using OrderFleet.Infrastructure.Operations;

namespace OrderFleet.Infrastructure.Messaging;

public sealed class RabbitConsumer(IOptions<RabbitOptions> options, IServiceScopeFactory scopes, RabbitDeliveryProcessor processor,
    WorkerProgress progress, IOptions<WorkerHealthOptions> health)
{
    public Task RunPaymentsAsync(CancellationToken ct) => RunAsync(RabbitTopology.Payments(options.Value.Prefix),
        sp => sp.GetRequiredService<CancelledPaymentEventHandler>(), "payments", ct);

    public Task RunDispatchAsync(CancellationToken ct) => RunAsync(RabbitTopology.Dispatch(options.Value.Prefix),
        sp => sp.GetRequiredService<DispatchEventHandler>(), "dispatch", ct);

    public Task RunRealtimeAsync(CancellationToken ct) => RunAsync(RabbitTopology.Realtime(options.Value.Prefix),
        sp => sp.GetRequiredService<RealtimeEventHandler>(), "realtime", ct);

    public Task RunFinancialAsync(CancellationToken ct) => RunAsync(RabbitTopology.Financial(options.Value.Prefix),
        sp => sp.GetRequiredService<ArchiveFinancialEvent>(), "financial", ct);

    private async Task RunAsync(string queue, Func<IServiceProvider, IIntegrationEventHandler> resolve, string worker, CancellationToken ct)
    {
        await using var session = await RabbitSession.OpenAsync(options.Value, ct);
        var channel = session.Channel;
        await RabbitTopology.DeclareAsync(channel, options.Value.Prefix, ct);
        await channel.BasicQosAsync(0, 10, false, ct);
        var stopped = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, _) => { stopped.TrySetResult(new IOException("Consumer channel closed.")); return Task.CompletedTask; };
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                progress.BeginWork(worker);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var scope = scopes.CreateScope();
                var handler = resolve(scope.ServiceProvider);
                await processor.ProcessAsync(channel, queue, delivery.DeliveryTag, delivery.BasicProperties,
                    delivery.Body, handler, timeout.Token);
                progress.Success(worker);
            }
            catch (Exception error) { stopped.TrySetResult(error); } // Supervisor logs once; disposal requeues unacked deliveries.
        };
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, ct);
        while (!ct.IsCancellationRequested)
        {
            var wake = Task.Delay(TimeSpan.FromSeconds(health.Value.ProbeSeconds), ct);
            if (await Task.WhenAny(stopped.Task, wake) == stopped.Task) throw await stopped.Task;
            await wake;
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(5));
            await channel.QueueDeclarePassiveAsync(queue, probe.Token);
            progress.Success(worker, idleProbe: true);
        }
    }
}
