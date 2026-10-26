using Microsoft.Extensions.Logging;
using OrderFleet.Application.Messaging;
using OrderFleet.Infrastructure.Messaging;
using OrderFleet.Infrastructure.Operations;
using RabbitMQ.Client;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "RabbitMQ")]
public sealed class RabbitProcessorNameTests(RabbitFixture broker) : IClassFixture<RabbitFixture>
{
    [Fact]
    public async Task ExplicitWorkerName_DoesNotDependOnQueueSuffix_AndPreservesPoisonRouting()
    {
        var options = new RabbitOptions { Uri = broker.ConnectionString, Prefix = "processor-" + Guid.NewGuid().ToString("N") };
        await using var session = await RabbitSession.OpenAsync(options, CancellationToken.None);
        var queue = options.Prefix + ".custom.v1";
        await session.Channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        await session.Channel.QueueDeclareAsync(RabbitTopology.Dead(queue), durable: true, exclusive: false, autoDelete: false);
        var id = Guid.NewGuid();
        await session.Channel.BasicPublishAsync("", queue, true,
            new BasicProperties { Persistent = true, MessageId = id.ToString("D"), Type = "order.cancelled.v1" }, "{}"u8.ToArray());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var delivery = await GetAsync(session.Channel, queue, deadline.Token);
        var logger = new CaptureLogger();
        var sentinel = new MustNotHandle();
        await new RabbitDeliveryProcessor(logger).ProcessAsync(session.Channel, queue, WorkerNames.Financial,
            delivery.DeliveryTag, delivery.BasicProperties, delivery.Body, sentinel, deadline.Token);
        Assert.Equal(0, sentinel.Calls);
        Assert.Equal(WorkerNames.Financial, Assert.Single(logger.Consumers));
        Assert.Null(await session.Channel.BasicGetAsync(queue, false, deadline.Token));
        var dead = await GetAsync(session.Channel, RabbitTopology.Dead(queue), deadline.Token);
        Assert.Equal(id.ToString("D"), dead.BasicProperties.MessageId);
        Assert.Equal(1, dead.BasicProperties.Headers!["x-retry-attempt"]);
        Assert.Equal("InvalidEnvelope", System.Text.Encoding.UTF8.GetString(Assert.IsType<byte[]>(dead.BasicProperties.Headers["x-failure-code"])));
        Assert.Equal("{}"u8.ToArray(), dead.Body.ToArray());
        await session.Channel.BasicAckAsync(dead.DeliveryTag, false, deadline.Token);
    }

    [Fact]
    public async Task UnroutableRetryCopy_LeavesOriginalUnacked_AndRedeliversAfterReconnect()
    {
        var options = new RabbitOptions { Uri = broker.ConnectionString, Prefix = "retry-return-" + Guid.NewGuid().ToString("N") };
        var queue = options.Prefix + ".custom.v1";
        var id = Guid.NewGuid().ToString("D");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var original = await RabbitSession.OpenAsync(options, deadline.Token))
        {
            await original.Channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: deadline.Token);
            // Deliberately omit this new test queue's dead-letter destination.
            await original.Channel.BasicPublishAsync("", queue, true,
                new BasicProperties { Persistent = true, MessageId = id, Type = "order.cancelled.v1" }, "{}"u8.ToArray(), deadline.Token);
            var delivery = await GetAsync(original.Channel, queue, deadline.Token);
            await Assert.ThrowsAsync<RabbitMQ.Client.Exceptions.PublishReturnException>(() =>
                new RabbitDeliveryProcessor().ProcessAsync(original.Channel, queue, WorkerNames.Financial,
                    delivery.DeliveryTag, delivery.BasicProperties, delivery.Body, new MustNotHandle(), deadline.Token));
        }
        await using var reconnected = await RabbitSession.OpenAsync(options, deadline.Token);
        var replay = await GetAsync(reconnected.Channel, queue, deadline.Token);
        Assert.Equal(id, replay.BasicProperties.MessageId);
        Assert.True(replay.Redelivered);
        Assert.Equal("{}"u8.ToArray(), replay.Body.ToArray());
        await reconnected.Channel.BasicAckAsync(replay.DeliveryTag, false, deadline.Token);
    }

    private static async Task<BasicGetResult> GetAsync(IChannel channel, string queue, CancellationToken ct)
    {
        while (true)
        {
            var delivery = await channel.BasicGetAsync(queue, false, ct);
            if (delivery is not null) return delivery;
            await Task.Delay(20, ct);
        }
    }
    private sealed class MustNotHandle : IIntegrationEventHandler
    {
        internal int Calls { get; private set; }
        public Task HandleAsync(IntegrationEvent message, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("An invalid envelope must not invoke the handler."); }
    }
    private sealed class CaptureLogger : ILogger<RabbitDeliveryProcessor>
    {
        internal List<string> Consumers { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(error);
            var values = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state).ToDictionary();
            Consumers.Add(Assert.IsType<string>(values["Consumer"]));
        }
    }
}
