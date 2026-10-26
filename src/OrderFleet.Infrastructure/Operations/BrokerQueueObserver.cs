using Microsoft.Extensions.Options;
using OrderFleet.Application.Operations;
using OrderFleet.Infrastructure.Messaging;

namespace OrderFleet.Infrastructure.Operations;

public sealed class BrokerQueueObserver(IOptions<RabbitOptions> options, TimeProvider time)
{
    private QueueDiagnostic[] snapshot = [];
    public IReadOnlyList<QueueDiagnostic> Snapshot() => Volatile.Read(ref snapshot);
    public async Task CollectAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using var session = await RabbitSession.OpenAsync(options.Value, timeout.Token);
        var queues = new[] { ("payments", RabbitTopology.Payments(options.Value.Prefix)), ("dispatch", RabbitTopology.Dispatch(options.Value.Prefix)),
            ("realtime", RabbitTopology.Realtime(options.Value.Prefix)), ("financial", RabbitTopology.Financial(options.Value.Prefix)) };
        var results = new List<QueueDiagnostic>();
        foreach (var (consumer, queue) in queues)
        {
            var names = new[] { ("main", queue), ("dead", RabbitTopology.Dead(queue)) }
                .Concat(Enumerable.Range(1, RabbitTopology.RetryDelaysMilliseconds.Length).Select(i => ("retry" + i, RabbitTopology.Retry(queue, i))));
            foreach (var (kind, name) in names)
            {
                var actual = await session.Channel.QueueDeclarePassiveAsync(name, timeout.Token);
                results.Add(new(consumer, kind, actual.MessageCount, actual.ConsumerCount, time.GetUtcNow()));
            }
        }
        Volatile.Write(ref snapshot, results.ToArray()); // Failed samples never fabricate zeros.
    }
}

public sealed class RuntimeDiagnostics(WorkerProgress workers, BrokerQueueObserver broker) : IRuntimeDiagnostics
{
    public RuntimeDiagnostic Snapshot() => new(workers.Snapshot(), broker.Snapshot());
}
