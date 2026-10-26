using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrderFleet.Application.Messaging;
using OrderFleet.Application.Payments;
using OrderFleet.Infrastructure.Messaging;
using OrderFleet.Infrastructure.Operations;
using OrderFleet.Infrastructure.Persistence;
using RabbitMQ.Client;
using Serilog.Core;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "RabbitMQ")]
public sealed class FinancialArchiveTests(PostgresFixture postgres, RabbitFixture rabbit) : IClassFixture<PostgresFixture>, IClassFixture<RabbitFixture>
{
    [Theory]
    [InlineData("0.01")]
    [InlineData("79228162514264337593543950335")]
    public async Task FinancialArchive_PreservesAllowedMonetaryBoundary(string value)
    {
        await using var factory = new ApiFactory(postgres);
        var amount = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        var fact = Fact("authorized", "Authorized", amount);
        await ArchiveAsync(factory, fact);
        await using var verify = postgres.CreateContext();
        Assert.Equal(amount, (await verify.FinancialAuditRecords.SingleAsync(x => x.MessageId == fact.MessageId)).Amount);
    }
    [Theory]
    [InlineData("authorized", "Authorized")]
    [InlineData("captured", "Captured")]
    [InlineData("failed", "Failed")]
    [InlineData("capture-uncertain", "PendingReconciliation")]
    [InlineData("voided", "Voided")]
    [InlineData("refunded", "Refunded")]
    public async Task AllFinancialFacts_AreImmutableAndConcurrentReplaySafe(string type, string state)
    {
        await using var factory = new ApiFactory(postgres);
        var fact = Fact(type, state);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ArchiveAsync(factory, fact)));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveAsync(factory, fact with
        { PayloadJson = fact.PayloadJson.Replace("25.5", "26.5", StringComparison.Ordinal) }));
        await using var verify = postgres.CreateContext();
        var record = await verify.FinancialAuditRecords.SingleAsync(x => x.MessageId == fact.MessageId);
        Assert.Equal(25.50m, record.Amount); Assert.Equal(state, record.State);
        Assert.Equal(1, await verify.InboxMessages.CountAsync(x => x.MessageId == fact.MessageId && x.Consumer == EfFinancialArchive.Consumer));
    }

    [Fact]
    public async Task CommitBeforeAck_RedeliversAndArchivesOneFact_WithoutChangingPayment()
    {
        await using var factory = new ApiFactory(postgres);
        var options = Broker(); var fact = Fact("authorized", "Authorized");
        await using var publisher = new RabbitEventPublisher(Options.Create(options));
        await publisher.PublishAsync(fact, CancellationToken.None); await publisher.PublishAsync(fact, CancellationToken.None);
        var queue = RabbitTopology.Financial(options.Prefix);
        await using (var first = await RabbitSession.OpenAsync(options, CancellationToken.None))
        {
            var message = await GetAsync(first.Channel, queue);
            await ArchiveAsync(factory, EventEnvelopeCodec.Decode(message.Body, message.BasicProperties.MessageId, message.BasicProperties.Type));
            // Crash gap: archive+Inbox committed; deliberately no broker ACK.
        }
        await using var resumed = await RabbitSession.OpenAsync(options, CancellationToken.None);
        var redelivered = await GetAsync(resumed.Channel, queue); Assert.True(redelivered.Redelivered);
        await ProcessAsync(factory, resumed.Channel, queue, redelivered);
        await ProcessAsync(factory, resumed.Channel, queue, await GetAsync(resumed.Channel, queue));
        Assert.Equal(0u, (await resumed.Channel.QueueDeclarePassiveAsync(queue)).MessageCount);
        await using var verify = postgres.CreateContext();
        Assert.Equal(1, await verify.FinancialAuditRecords.CountAsync(x => x.MessageId == fact.MessageId));
        Assert.False(await verify.Payments.AnyAsync(x => x.OrderId == fact.AggregateId));
    }

    [Fact]
    public async Task FailedArchive_RetriesDurably_AndPoisonDoesNotBlockFollowingFact()
    {
        await using var factory = new ApiFactory(postgres);
        var logs = new CapturedLogs();
        factory.CustomizeServices = services => services.AddSingleton<ILogEventSink>(logs);
        var options = Broker(); var fact = Fact("captured", "Captured"); var queue = RabbitTopology.Financial(options.Prefix);
        await using var publisher = new RabbitEventPublisher(Options.Create(options)); await publisher.PublishAsync(fact, CancellationToken.None);
        await using var session = await RabbitSession.OpenAsync(options, CancellationToken.None);
        var first = await GetAsync(session.Channel, queue);
        await factory.Services.GetRequiredService<RabbitDeliveryProcessor>().ProcessAsync(session.Channel, queue, first.DeliveryTag, first.BasicProperties, first.Body, new Failure(), CancellationToken.None);
        await using (var verify = postgres.CreateContext()) Assert.False(await verify.FinancialAuditRecords.AnyAsync(x => x.MessageId == fact.MessageId));
        Assert.Equal(1u, (await session.Channel.QueueDeclarePassiveAsync(RabbitTopology.Retry(queue, 1))).MessageCount);
        var retried = await GetAsync(session.Channel, queue); Assert.Equal(1, retried.BasicProperties.Headers!["x-retry-attempt"]);
        await ProcessAsync(factory, session.Channel, queue, retried);
        var poison = Fact("captured", "Captured") with { PayloadJson = "[]" };
        await session.Channel.BasicPublishAsync("", queue, true, new BasicProperties
        { Persistent = true, MessageId = poison.MessageId.ToString("D"), Type = poison.Type }, JsonSerializer.SerializeToUtf8Bytes(poison, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await publisher.PublishAsync(Fact("refunded", "Refunded"), CancellationToken.None);
        await ProcessAsync(factory, session.Channel, queue, await GetAsync(session.Channel, queue));
        var dead = await GetAsync(session.Channel, RabbitTopology.Dead(queue));
        Assert.Equal(1, dead.BasicProperties.Headers!["x-retry-attempt"]);
        Assert.Equal(poison.MessageId.ToString("D"), dead.BasicProperties.MessageId);
        await ProcessAsync(factory, session.Channel, queue, await GetAsync(session.Channel, queue));
        var exhausted = Fact("failed", "Failed");
        await session.Channel.BasicPublishAsync("", queue, true, new BasicProperties
        {
            Persistent = true,
            MessageId = exhausted.MessageId.ToString("D"),
            Type = exhausted.Type,
            Headers = new Dictionary<string, object?> { ["x-retry-attempt"] = 4 }
        }, EventEnvelopeCodec.Encode(exhausted));
        var exhaustedDelivery = await GetAsync(session.Channel, queue);
        await factory.Services.GetRequiredService<RabbitDeliveryProcessor>().ProcessAsync(session.Channel, queue, exhaustedDelivery.DeliveryTag,
            exhaustedDelivery.BasicProperties, exhaustedDelivery.Body, new Failure(), CancellationToken.None);
        var observer = new BrokerQueueObserver(Options.Create(options), TimeProvider.System);
        await observer.CollectAsync(CancellationToken.None);
        Assert.Equal(1u, observer.Snapshot().Single(x => x.Consumer == "financial" && x.Kind == "dead").ReadyMessages);
        var exhaustedDead = await GetAsync(session.Channel, RabbitTopology.Dead(queue));
        Assert.Equal(exhausted.MessageId.ToString("D"), exhaustedDead.BasicProperties.MessageId);
        Assert.Equal(5, exhaustedDead.BasicProperties.Headers!["x-retry-attempt"]);
        await using var final = postgres.CreateContext();
        Assert.True(await final.FinancialAuditRecords.AnyAsync(x => x.MessageId == fact.MessageId));
        Assert.False(await final.InboxMessages.AnyAsync(x => x.MessageId == poison.MessageId));
        Assert.Equal(3, logs.Events.Count(x => x.Contains("Consumer {Consumer} failed", StringComparison.Ordinal)));
        Assert.Contains(logs.Events, x => x.Contains("InvalidEnvelope", StringComparison.Ordinal));
        Assert.DoesNotContain("sensitive-provider-secret", string.Join('\n', logs.Events));
    }

    [Fact]
    public async Task HostedFinancialConsumer_DrainsObservedBacklogAfterRestart_AndArchivesRealPaymentFacts()
    {
        var options = Broker();
        var first = new ApiFactory(postgres);
        Guid id;
        try
        {
            using var owner = await ClientAsync(first, "RestaurantOwner"); var (restaurant, product) = await CatalogAsync(owner);
            using var customer = await ClientAsync(first, "Customer");
            var created = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/orders/{id}/accept", null)).StatusCode);
            await using var context = postgres.CreateContext();
            var facts = await context.OutboxMessages.Where(x => x.AggregateId == id && x.Type.StartsWith("payment.")).ToArrayAsync();
            Assert.Equal(2, facts.Length);
            await using var publisher = new RabbitEventPublisher(Options.Create(options));
            foreach (var row in facts) await publisher.PublishAsync(new(row.Id, row.Type, row.Version, row.OccurredAtUtc, row.AggregateId, row.CorrelationId, row.PayloadJson), CancellationToken.None);
            var observer = new BrokerQueueObserver(Options.Create(options), TimeProvider.System); await observer.CollectAsync(CancellationToken.None);
            Assert.Equal(2u, observer.Snapshot().Single(x => x.Consumer == "financial" && x.Kind == "main").ReadyMessages);
        }
        finally { await first.DisposeAsync(); }
        await using var restarted = new ApiFactory(postgres);
        restarted.Overrides["Messaging:Enabled"] = "true"; restarted.Overrides["Messaging:Uri"] = options.Uri; restarted.Overrides["Messaging:Prefix"] = options.Prefix;
        using var client = restarted.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            await using var verify = postgres.CreateContext();
            if (await verify.FinancialAuditRecords.CountAsync(x => x.OrderId == id, timeout.Token) == 2) break;
            await Task.Delay(50, timeout.Token);
        }
        var sampled = restarted.Services.GetRequiredService<BrokerQueueObserver>(); await sampled.CollectAsync(timeout.Token);
        Assert.Equal(0u, sampled.Snapshot().Single(x => x.Consumer == "financial" && x.Kind == "main").ReadyMessages);
        Assert.Equal(1u, sampled.Snapshot().Single(x => x.Consumer == "financial" && x.Kind == "main").Consumers);
    }

    private RabbitOptions Broker() => new() { Uri = rabbit.ConnectionString, Prefix = "financial" + Guid.NewGuid().ToString("N") };
    private static IntegrationEvent Fact(string type, string state, decimal amount = 25.50m)
    {
        var order = Guid.NewGuid();
        return new(Guid.NewGuid(), "payment." + type + ".v1", 1, DateTimeOffset.UtcNow, order, null,
            JsonSerializer.Serialize(new FinancialFact(Guid.NewGuid(), order, Guid.NewGuid(), amount, "SAR", state), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
    private static async Task ArchiveAsync(ApiFactory factory, IntegrationEvent fact)
    { using var scope = factory.Services.CreateScope(); await scope.ServiceProvider.GetRequiredService<ArchiveFinancialEvent>().HandleAsync(fact, CancellationToken.None); }
    private static async Task ProcessAsync(ApiFactory factory, IChannel channel, string queue, BasicGetResult message)
    { using var scope = factory.Services.CreateScope(); await factory.Services.GetRequiredService<RabbitDeliveryProcessor>().ProcessAsync(channel, queue, message.DeliveryTag, message.BasicProperties, message.Body, scope.ServiceProvider.GetRequiredService<ArchiveFinancialEvent>(), CancellationToken.None); }
    private static async Task<BasicGetResult> GetAsync(IChannel channel, string queue)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true) { var message = await channel.BasicGetAsync(queue, false, timeout.Token); if (message is not null) return message; await Task.Delay(50, timeout.Token); }
    }
    private sealed class Failure : IIntegrationEventHandler
    { public Task HandleAsync(IntegrationEvent message, CancellationToken cancellationToken) => throw new IOException("sensitive-provider-secret"); }
}
