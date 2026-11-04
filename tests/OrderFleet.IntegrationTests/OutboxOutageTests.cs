using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderFleet.Application.Messaging;
using OrderFleet.Domain.Payments;
using OrderFleet.Infrastructure.Clock;
using OrderFleet.Infrastructure.Messaging;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "RabbitMQ")]
public sealed class OutboxOutageTests(PostgresFixture postgres, RabbitFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitFixture>
{
    [Fact]
    public async Task ProlongedBrokerOutage_ExceedsOldBudget_AndPublishesAfterRecovery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var payment = Payment.Authorize(Guid.NewGuid(), Guid.NewGuid(), 5m, "SAR", clock.GetUtcNow());
        var fact = IntegrationEvents.FromPayment(payment, "payment.authorized.v1", clock.GetUtcNow());
        await using (var context = postgres.CreateContext())
        {
            context.OutboxMessages.Add(new(fact));
            await context.SaveChangesAsync(timeout.Token);
        }
        var settings = new RabbitOptions
        {
            Uri = rabbit.ConnectionString, Prefix = "outage-" + Guid.NewGuid().ToString("N"),
            OutboxMaxAttempts = 10, PublishTimeoutSeconds = 1
        };
        var store = new PostgresOutboxStore(new TestContextFactory(postgres));
        await rabbit.StopAsync();
        try
        {
            await using var publisher = new RabbitEventPublisher(Options.Create(settings));
            var relay = new OutboxRelay(store, publisher, new SystemClock(clock), Options.Create(settings), NullLogger<OutboxRelay>.Instance);
            for (var attempt = 1; attempt <= 15; attempt++)
            {
                Assert.Equal(1, await relay.RunBatchAsync(timeout.Token));
                await using var verify = postgres.CreateContext();
                var row = await verify.OutboxMessages.SingleAsync(x => x.Id == fact.MessageId, timeout.Token);
                Assert.Equal(attempt, row.Attempts);
                Assert.Null(row.DeadLetteredAtUtc);
                Assert.Null(row.PublishedAtUtc);
                Assert.Null(row.LeaseId);
                Assert.Equal("BrokerUnavailable", row.LastErrorCode);
                Assert.InRange(row.NextAttemptAtUtc - clock.GetUtcNow(), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(300));
                Assert.Empty(await store.ClaimAsync(1, clock.GetUtcNow(), TimeSpan.FromSeconds(60), timeout.Token));
                clock.Advance(row.NextAttemptAtUtc - clock.GetUtcNow());
            }
        }
        finally { await rabbit.StartAsync(); }
        settings.Uri = rabbit.ConnectionString;
        settings.PublishTimeoutSeconds = 5;
        await using var recoveredPublisher = new RabbitEventPublisher(Options.Create(settings));
        var recovered = new OutboxRelay(store, recoveredPublisher, new SystemClock(clock), Options.Create(settings), NullLogger<OutboxRelay>.Instance);
        Assert.Equal(1, await recovered.RunBatchAsync(timeout.Token));
        await using var final = postgres.CreateContext();
        var published = await final.OutboxMessages.SingleAsync(x => x.Id == fact.MessageId, timeout.Token);
        Assert.NotNull(published.PublishedAtUtc);
        Assert.Null(published.DeadLetteredAtUtc);
        Assert.Equal(16, published.Attempts);
        await using var session = await RabbitSession.OpenAsync(settings, timeout.Token);
        var delivery = await session.Channel.BasicGetAsync(RabbitTopology.Financial(settings.Prefix), false, timeout.Token);
        Assert.NotNull(delivery);
        Assert.Equal(fact.MessageId, EventEnvelopeCodec.Decode(delivery.Body, delivery.BasicProperties.MessageId, delivery.BasicProperties.Type).MessageId);
        await session.Channel.BasicAckAsync(delivery.DeliveryTag, false, timeout.Token);
    }
}
