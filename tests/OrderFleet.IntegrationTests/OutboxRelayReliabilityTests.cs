using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OrderFleet.Application.Messaging;
using OrderFleet.Application.Payments;
using OrderFleet.Domain.Payments;
using OrderFleet.Infrastructure.Clock;
using OrderFleet.Infrastructure.Messaging;
using OrderFleet.Infrastructure.Persistence;
using OrderFleet.Infrastructure.Reliability;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "RabbitMQ")]
public sealed class OutboxRelayReliabilityTests(PostgresFixture postgres, RabbitFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitFixture>
{
    [Fact]
    [Trait("Phase", "2")]
    public async Task ConfirmedPublication_CompletionFailureRetainsLease_AndRedeliveryArchivesOnce()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var systemClock = new SystemClock(clock);
        var fact = await InsertFactAsync(clock.GetUtcNow());
        var store = new CompletionFailureStore(new PostgresOutboxStore(new TestContextFactory(postgres)));
        var settings = new RabbitOptions { Uri = rabbit.ConnectionString, Prefix = "completion-" + Guid.NewGuid().ToString("N"), OutboxMaxAttempts = 1 };
        await using var publisher = new RabbitEventPublisher(Options.Create(settings));
        var relay = new OutboxRelay(store, publisher, systemClock, Options.Create(settings), NullLogger<OutboxRelay>.Instance);
        Assert.Equal(1, await relay.RunBatchAsync(CancellationToken.None));
        Assert.Equal(0, store.Failures);
        await using (var verify = postgres.CreateContext())
        {
            var row = await verify.OutboxMessages.SingleAsync(x => x.Id == fact.MessageId);
            Assert.Null(row.PublishedAtUtc);
            Assert.Null(row.DeadLetteredAtUtc);
            Assert.Null(row.LastErrorCode);
            Assert.NotNull(row.LeaseId);
            Assert.Equal(1, row.Attempts);
        }
        clock.Advance(TimeSpan.FromSeconds(settings.LeaseSeconds + 1));
        Assert.Equal(1, await relay.RunBatchAsync(CancellationToken.None));
        Assert.Equal(0, store.Failures);
        await using var session = await RabbitSession.OpenAsync(settings, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        for (var i = 0; i < 2; i++)
        {
            RabbitMQ.Client.BasicGetResult? delivery;
            while ((delivery = await session.Channel.BasicGetAsync(RabbitTopology.Financial(settings.Prefix), false, timeout.Token)) is null)
                await Task.Delay(20, timeout.Token);
            var message = EventEnvelopeCodec.Decode(delivery.Body, delivery.BasicProperties.MessageId, delivery.BasicProperties.Type);
            Assert.Equal(fact.MessageId, message.MessageId);
            await using var context = postgres.CreateContext();
            await new ArchiveFinancialEvent(new EfFinancialArchive(context, systemClock)).HandleAsync(message, timeout.Token);
            await session.Channel.BasicAckAsync(delivery.DeliveryTag, false, timeout.Token);
        }
        await using var final = postgres.CreateContext();
        Assert.NotNull((await final.OutboxMessages.SingleAsync(x => x.Id == fact.MessageId)).PublishedAtUtc);
        Assert.Equal(1, await final.FinancialAuditRecords.CountAsync(x => x.MessageId == fact.MessageId));
        Assert.Equal(1, await final.InboxMessages.CountAsync(x => x.MessageId == fact.MessageId && x.Consumer == EfFinancialArchive.Consumer));
    }

    [Fact]
    public async Task PublicationFailure_RecordsBackoffWithoutMarkingPublished()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var fact = await InsertFactAsync(clock.GetUtcNow());
        var store = new CompletionFailureStore(new PostgresOutboxStore(new TestContextFactory(postgres))) { ThrowOnComplete = false };
        var relay = new OutboxRelay(store, new UnavailablePublisher(), new SystemClock(clock),
            Options.Create(new RabbitOptions { OutboxMaxAttempts = 2 }), NullLogger<OutboxRelay>.Instance);
        Assert.Equal(1, await relay.RunBatchAsync(CancellationToken.None));
        Assert.Equal(1, store.Failures);
        await using var verify = postgres.CreateContext();
        var row = await verify.OutboxMessages.SingleAsync(x => x.Id == fact.MessageId);
        Assert.Equal(1, row.Attempts);
        Assert.Equal("BrokerUnavailable", row.LastErrorCode);
        Assert.True(row.NextAttemptAtUtc > clock.GetUtcNow());
        Assert.Null(row.PublishedAtUtc);
        Assert.Null(row.DeadLetteredAtUtc);

        var retryAt = row.NextAttemptAtUtc;
        var retry = Assert.Single(await store.ClaimAsync(1, retryAt, TimeSpan.FromSeconds(60), CancellationToken.None));
        Assert.True(await store.FailAsync(retry, retryAt, "InvalidEnvelope", 2, CancellationToken.None));
    }

    [Fact]
    public async Task PersistedFailure_UsesInjectedRetryPolicy()
    {
        var instant = DateTimeOffset.UtcNow;
        var now = new DateTimeOffset(instant.Ticks - instant.Ticks % 10, TimeSpan.Zero);
        var fact = await InsertFactAsync(now);
        var store = new PostgresOutboxStore(new TestContextFactory(postgres), new FixedBackoff());
        var lease = Assert.Single(await store.ClaimAsync(1, now, TimeSpan.FromSeconds(60), CancellationToken.None));
        Assert.True(await store.FailAsync(lease, now, "BrokerUnavailable", 2, CancellationToken.None));
        await using var verify = postgres.CreateContext();
        var row = await verify.OutboxMessages.SingleAsync(x => x.Id == fact.MessageId);
        Assert.Equal(now.AddSeconds(17), row.NextAttemptAtUtc);
        var retry = Assert.Single(await store.ClaimAsync(1, row.NextAttemptAtUtc, TimeSpan.FromSeconds(60), CancellationToken.None));
        Assert.True(await store.FailAsync(retry, row.NextAttemptAtUtc, "InvalidEnvelope", 2, CancellationToken.None));
    }

    private sealed class FixedBackoff : IRetryBackoff
    {
        public TimeSpan GetDelay(int attempt, TimeSpan initial, TimeSpan maximum) => TimeSpan.FromSeconds(17);
    }

    private async Task<IntegrationEvent> InsertFactAsync(DateTimeOffset now)
    {
        var payment = Payment.Authorize(Guid.NewGuid(), Guid.NewGuid(), 1.25m, "SAR", now);
        var fact = IntegrationEvents.FromPayment(payment, "payment.authorized.v1", now);
        await using var context = postgres.CreateContext();
        context.OutboxMessages.Add(new(fact));
        await context.SaveChangesAsync();
        return fact;
    }

    private sealed class CompletionFailureStore(IOutboxStore inner) : IOutboxStore
    {
        internal bool ThrowOnComplete { get; set; } = true;
        internal int Failures { get; private set; }
        public Task<IReadOnlyList<OutboxLease>> ClaimAsync(int count, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct) =>
            inner.ClaimAsync(count, now, leaseDuration, ct);
        public Task<bool> CompleteAsync(OutboxLease lease, DateTimeOffset now, CancellationToken ct)
        {
            if (ThrowOnComplete) { ThrowOnComplete = false; throw new IOException("Injected completion failure"); }
            return inner.CompleteAsync(lease, now, ct);
        }
        public Task<bool> FailAsync(OutboxLease lease, DateTimeOffset now, string errorCode, int maxAttempts, CancellationToken ct)
        {
            Failures++;
            return inner.FailAsync(lease, now, errorCode, maxAttempts, ct);
        }
    }

    private sealed class UnavailablePublisher : IEventPublisher
    {
        public Task PublishAsync(IntegrationEvent message, CancellationToken ct) => Task.FromException(new IOException("Injected publisher failure"));
    }
}
