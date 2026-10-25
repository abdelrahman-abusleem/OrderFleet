using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Messaging;
using OrderFleet.Application.Payments;
using OrderFleet.Domain.Payments;
using OrderFleet.Infrastructure.Clock;
using OrderFleet.Infrastructure.Messaging;
using RabbitMQ.Client;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "RabbitMQ")]
public sealed class RabbitDeliveryTests(PostgresFixture postgres, RabbitFixture rabbit) : IClassFixture<PostgresFixture>, IClassFixture<RabbitFixture>
{
    [Fact]
    public async Task AcceptedEvent_DispatchesThroughHostedConsumer_AndCourierCompletesDelivery()
    {
        var options = Options();
        await using var factory = new ApiFactory(postgres);
        factory.Overrides["Messaging:Enabled"] = "true";
        factory.Overrides["Messaging:Uri"] = options.Uri;
        factory.Overrides["Messaging:Prefix"] = options.Prefix;
        using var courier = await DispatchConcurrencyTests.CourierAsync(factory, 60);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await DispatchConcurrencyTests.CatalogForDispatchAsync(owner, 60);
        var id = await DispatchConcurrencyTests.AcceptedAsync(factory, owner, restaurant, product);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Guid assignment;
        while (true)
        {
            await using var context = postgres.CreateContext();
            var order = await context.Orders.SingleAsync(x => x.Id == id, timeout.Token);
            if (order.Status == OrderFleet.Domain.Orders.OrderStatus.Assigned) { assignment = order.AssignmentId!.Value; break; }
            await Task.Delay(50, timeout.Token);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await courier.PostAsJsonAsync($"/api/v1/orders/{id}/pickup", new { assignmentId = assignment })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await courier.PostAsJsonAsync($"/api/v1/orders/{id}/deliver", new { assignmentId = assignment })).StatusCode);
        await using var verify = postgres.CreateContext();
        var acceptedEvent = await verify.OutboxMessages.SingleAsync(x => x.AggregateId == id && x.Type == "order.accepted.v1");
        Assert.True(await verify.InboxMessages.AnyAsync(x => x.MessageId == acceptedEvent.Id && x.Consumer == "dispatch.v1"));
        Assert.Equal(OrderFleet.Domain.Orders.OrderStatus.Delivered, (await verify.Orders.SingleAsync(x => x.Id == id)).Status);
    }

    [Fact]
    public async Task BrokerOutageAfterOrderCommit_BacklogsAndPublishesAfterRestart()
    {
        await using var factory = new ApiFactory(postgres);
        var orderId = await CancelledOrderAsync(factory);
        var options = Options();
        options.PublishTimeoutSeconds = 1;
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var store = new PostgresOutboxStore(new TestContextFactory(postgres));
        await rabbit.StopAsync();
        try
        {
            await using var unavailable = new RabbitEventPublisher(Microsoft.Extensions.Options.Options.Create(options));
            var relay = new OutboxRelay(store, unavailable, new SystemClock(clock),
                Microsoft.Extensions.Options.Options.Create(options), NullLogger<OutboxRelay>.Instance);
            Assert.True(await relay.RunBatchAsync(CancellationToken.None) > 0);
            await using var context = postgres.CreateContext();
            Assert.True(await context.Orders.AnyAsync(x => x.Id == orderId));
            var pending = await context.OutboxMessages.SingleAsync(x => x.AggregateId == orderId && x.Type == "order.cancelled.v1");
            Assert.Null(pending.PublishedAtUtc);
            Assert.Equal("BrokerUnavailable", pending.LastErrorCode);
        }
        finally { await rabbit.StartAsync(); }
        options.Uri = rabbit.ConnectionString;
        options.PublishTimeoutSeconds = 5;
        clock.Advance(TimeSpan.FromSeconds(5));
        await using var publisher = new RabbitEventPublisher(Microsoft.Extensions.Options.Options.Create(options));
        var recovered = new OutboxRelay(store, publisher, new SystemClock(clock),
            Microsoft.Extensions.Options.Options.Create(options), NullLogger<OutboxRelay>.Instance);
        Assert.True(await recovered.RunBatchAsync(CancellationToken.None) > 0);
        await using var session = await RabbitSession.OpenAsync(options, CancellationToken.None);
        var delivered = await GetAsync(session.Channel, RabbitTopology.Payments(options.Prefix));
        Assert.Equal(orderId, EventEnvelopeCodec.Decode(delivered.Body, delivered.BasicProperties.MessageId, delivered.BasicProperties.Type).AggregateId);
        await using var verify = postgres.CreateContext();
        Assert.NotNull((await verify.OutboxMessages.SingleAsync(x => x.AggregateId == orderId && x.Type == "order.cancelled.v1")).PublishedAtUtc);
    }

    [Fact]
    public async Task PublishBeforeMark_AndConsumerCommitBeforeAck_ReplayWithoutDuplicateSettlement()
    {
        await using var factory = new ApiFactory(postgres);
        var orderId = await CancelledOrderAsync(factory);
        var options = Options();
        var store = new PostgresOutboxStore(new TestContextFactory(postgres));
        var now = DateTimeOffset.UtcNow;
        var claims = await store.ClaimAsync(100, now, TimeSpan.FromSeconds(60), CancellationToken.None);
        var lease = Assert.Single(claims, x => x.Message.AggregateId == orderId && x.Message.Type == "order.cancelled.v1");
        await using var publisher = new RabbitEventPublisher(Microsoft.Extensions.Options.Options.Create(options));
        await publisher.PublishAsync(lease.Message, CancellationToken.None);
        // Crash gap: confirmed publication, but no Outbox completion.
        await using (var channelSession = await RabbitSession.OpenAsync(options, CancellationToken.None))
        {
            var delivery = await GetAsync(channelSession.Channel, RabbitTopology.Payments(options.Prefix));
            var envelope = EventEnvelopeCodec.Decode(delivery.Body, delivery.BasicProperties.MessageId, delivery.BasicProperties.Type);
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CancelledPaymentEventHandler>().HandleAsync(envelope, CancellationToken.None);
            // Crash gap: committed payment/Inbox, deliberately omit broker ACK.
        }
        await using var resumed = await RabbitSession.OpenAsync(options, CancellationToken.None);
        var redelivery = await GetAsync(resumed.Channel, RabbitTopology.Payments(options.Prefix));
        Assert.True(redelivery.Redelivered);
        await ProcessAsync(factory, options, resumed.Channel, redelivery);
        await publisher.PublishAsync(lease.Message, CancellationToken.None);
        await ProcessAsync(factory, options, resumed.Channel, await GetAsync(resumed.Channel, RabbitTopology.Payments(options.Prefix)));
        Assert.True(await store.CompleteAsync(lease, DateTimeOffset.UtcNow, CancellationToken.None));
        await using var verify = postgres.CreateContext();
        var payment = await verify.Payments.SingleAsync(x => x.OrderId == orderId);
        Assert.Equal(PaymentState.Voided, payment.State);
        Assert.Equal(1, await verify.MockPaymentEffects.CountAsync(x => x.AuthorizationId == payment.AuthorizationId && x.Operation == "Void"));
        Assert.Equal(1, await verify.InboxMessages.CountAsync(x => x.MessageId == lease.Message.MessageId && x.Consumer == "payments.cancelled.v1"));
    }

    [Fact]
    public async Task RetryQueue_DelaysDelivery_AndExhaustedOrMalformedMessageGoesToDeadLetter()
    {
        await using var factory = new ApiFactory(postgres);
        var id = await CancelledOrderAsync(factory);
        await using var context = postgres.CreateContext();
        var row = await context.OutboxMessages.SingleAsync(x => x.AggregateId == id && x.Type == "order.cancelled.v1");
        var message = new IntegrationEvent(row.Id, row.Type, row.Version, row.OccurredAtUtc, row.AggregateId, row.CorrelationId, row.PayloadJson, row.AggregateSequence);
        var options = Options();
        await using var publisher = new RabbitEventPublisher(Microsoft.Extensions.Options.Options.Create(options));
        await publisher.PublishAsync(message, CancellationToken.None);
        await using var session = await RabbitSession.OpenAsync(options, CancellationToken.None);
        var queue = RabbitTopology.Payments(options.Prefix);
        var first = await GetAsync(session.Channel, queue);
        using var scope = factory.Services.CreateScope();
        var actual = scope.ServiceProvider.GetRequiredService<CancelledPaymentEventHandler>();
        var processor = new RabbitDeliveryProcessor();
        await processor.ProcessAsync(session.Channel, queue, first.DeliveryTag, first.BasicProperties, first.Body,
            new DependencyFailure(), CancellationToken.None);
        Assert.Null(await session.Channel.BasicGetAsync(queue, autoAck: false));
        var retried = await GetAsync(session.Channel, queue);
        Assert.Equal(1, retried.BasicProperties.Headers!["x-retry-attempt"]);
        await processor.ProcessAsync(session.Channel, queue, retried.DeliveryTag, retried.BasicProperties, retried.Body, actual, CancellationToken.None);
        var exhausted = new BasicProperties
        {
            Persistent = true, MessageId = message.MessageId.ToString("D"), Type = message.Type,
            Headers = new Dictionary<string, object?> { ["x-retry-attempt"] = 4 }
        };
        await session.Channel.BasicPublishAsync("", queue, true, exhausted, EventEnvelopeCodec.Encode(message));
        var last = await GetAsync(session.Channel, queue);
        await processor.ProcessAsync(session.Channel, queue, last.DeliveryTag, last.BasicProperties, last.Body, new DependencyFailure(), CancellationToken.None);
        var dead = await GetAsync(session.Channel, RabbitTopology.Dead(queue));
        Assert.Equal(5, dead.BasicProperties.Headers!["x-retry-attempt"]);
        await session.Channel.BasicAckAsync(dead.DeliveryTag, false);
        var poisonId = Guid.NewGuid().ToString("D");
        await session.Channel.BasicPublishAsync("", queue, true,
            new BasicProperties { Persistent = true, MessageId = poisonId, Type = "order.cancelled.v1" }, "{}"u8.ToArray());
        var poison = await GetAsync(session.Channel, queue);
        await processor.ProcessAsync(session.Channel, queue, poison.DeliveryTag, poison.BasicProperties, poison.Body, actual, CancellationToken.None);
        var poisonDead = await GetAsync(session.Channel, RabbitTopology.Dead(queue));
        Assert.Equal(poisonId, poisonDead.BasicProperties.MessageId);
        Assert.Empty(await context.InboxMessages.Where(x => x.MessageId == Guid.Parse(poisonId)).ToArrayAsync());
    }

    [Fact]
    public async Task HostedRelayAndConsumer_SettleCancelledOrder_WithoutManualPumping()
    {
        var options = Options();
        await using var factory = new ApiFactory(postgres);
        factory.Overrides["Messaging:Enabled"] = "true";
        factory.Overrides["Messaging:Uri"] = options.Uri;
        factory.Overrides["Messaging:Prefix"] = options.Prefix;
        var id = await CancelledOrderAsync(factory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var context = postgres.CreateContext();
            var payment = await context.Payments.SingleAsync(x => x.OrderId == id, timeout.Token);
            if (payment.State == PaymentState.Voided
                && await context.OutboxMessages.AnyAsync(x => x.AggregateId == id && x.Type == "order.cancelled.v1" && x.PublishedAtUtc != null, timeout.Token)) break;
            await Task.Delay(50, timeout.Token);
        }
    }

    [Fact]
    public async Task MandatoryUnroutablePublish_IsNotReportedAsSuccess()
    {
        var options = Options();
        await using var publisher = new RabbitEventPublisher(Microsoft.Extensions.Options.Options.Create(options));
        var id = Guid.NewGuid();
        var message = new IntegrationEvent(Guid.NewGuid(), "payment.authorized.v1", 1, DateTimeOffset.UtcNow, id, null,
            JsonSerializer.Serialize(new { orderId = id }));
        await publisher.PublishAsync(message, CancellationToken.None); // Establish durable topology.
        await using var session = await RabbitSession.OpenAsync(options, CancellationToken.None);
        await session.Channel.QueueDeleteAsync(RabbitTopology.Financial(options.Prefix), ifUnused: false, ifEmpty: false);
        await Assert.ThrowsAsync<RabbitMQ.Client.Exceptions.PublishReturnException>(() => publisher.PublishAsync(message with { MessageId = Guid.NewGuid() }, CancellationToken.None));
    }

    private RabbitOptions Options() => new() { Uri = rabbit.ConnectionString, Prefix = "test" + Guid.NewGuid().ToString("N") };

    private static async Task<Guid> CancelledOrderAsync(ApiFactory factory)
    {
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        var response = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await customer.PostAsync($"/api/v1/orders/{id}/cancel", null)).StatusCode);
        return id;
    }

    private static async Task ProcessAsync(ApiFactory factory, RabbitOptions options, IChannel channel, BasicGetResult delivery)
    {
        using var scope = factory.Services.CreateScope();
        await new RabbitDeliveryProcessor().ProcessAsync(channel, RabbitTopology.Payments(options.Prefix), delivery.DeliveryTag,
            delivery.BasicProperties, delivery.Body, scope.ServiceProvider.GetRequiredService<CancelledPaymentEventHandler>(), CancellationToken.None);
    }

    private static async Task<BasicGetResult> GetAsync(IChannel channel, string queue)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var delivery = await channel.BasicGetAsync(queue, autoAck: false, timeout.Token);
            if (delivery is not null) return delivery;
            await Task.Delay(50, timeout.Token);
        }
    }

    private sealed class DependencyFailure : IIntegrationEventHandler
    {
        public Task HandleAsync(IntegrationEvent message, CancellationToken cancellationToken) => throw new DependencyUnavailableException();
    }
}
