using Microsoft.EntityFrameworkCore;
using OrderFleet.Infrastructure.Messaging;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class OutboxLeaseTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task TransactionRolledBackBeforeCommit_PersistsNeitherOrderNorItsEvent()
    {
        var order = OrderFleet.Domain.Orders.Order.Create(Guid.NewGuid(), Guid.NewGuid(),
            [new(Guid.NewGuid(), "Tea", 3.25m, 1)], new(99, 20, 9999.99m, 99999.99m), DateTimeOffset.UtcNow);
        await using (var context = fixture.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            context.Orders.Add(order);
            await new OrderFleet.Infrastructure.Persistence.EfOrderingStore(context).SaveAsync(CancellationToken.None);
            Assert.True(await context.OutboxMessages.AnyAsync(x => x.AggregateId == order.Id));
            await transaction.RollbackAsync();
        }
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.Orders.AnyAsync(x => x.Id == order.Id));
        Assert.False(await verify.OutboxMessages.AnyAsync(x => x.AggregateId == order.Id));
    }

    [Fact]
    public async Task ParallelClaims_AreDisjoint_AndExpiredWorkerCannotCompleteNewLease()
    {
        var now = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        await using (var context = fixture.CreateContext())
        {
            context.OutboxMessages.AddRange(ids.Select(id => new OutboxMessage(
                new(id, "order.created.v1", 1, now, Guid.NewGuid(), null, "{}"))));
            await context.SaveChangesAsync();
        }
        var store = new PostgresOutboxStore(new TestContextFactory(fixture));
        var claims = await Task.WhenAll(store.ClaimAsync(6, now, TimeSpan.FromSeconds(10), CancellationToken.None),
            store.ClaimAsync(6, now, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Equal(12, claims.SelectMany(x => x).Select(x => x.Message.MessageId).Distinct().Count());
        var old = claims[0][0];
        Assert.Empty(await store.ClaimAsync(12, now.AddSeconds(9), TimeSpan.FromSeconds(10), CancellationToken.None));
        var recovered = await store.ClaimAsync(12, now.AddSeconds(11), TimeSpan.FromSeconds(10), CancellationToken.None);
        var current = Assert.Single(recovered, x => x.Message.MessageId == old.Message.MessageId);
        Assert.NotEqual(old.LeaseId, current.LeaseId);
        Assert.False(await store.CompleteAsync(old, now.AddSeconds(12), CancellationToken.None));
        Assert.True(await store.CompleteAsync(current, now.AddSeconds(12), CancellationToken.None));
        Assert.False(await store.FailAsync(old, now.AddSeconds(12), "BrokerUnavailable", 3, CancellationToken.None));
        await using var verify = fixture.CreateContext();
        var row = await verify.OutboxMessages.SingleAsync(x => x.Id == old.Message.MessageId);
        Assert.NotNull(row.PublishedAtUtc);
        Assert.Null(row.LeaseId);
        foreach (var lease in recovered.Where(x => x.Message.MessageId != current.Message.MessageId))
            Assert.True(await store.CompleteAsync(lease, now.AddSeconds(12), CancellationToken.None));
    }

    [Fact]
    public async Task PublishFailures_BackOff_AndBecomeVisibleDeadLettersAtBound()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        await using (var context = fixture.CreateContext())
        {
            context.OutboxMessages.Add(new(new(id, "order.created.v1", 1, now, Guid.NewGuid(), null, "{}")));
            await context.SaveChangesAsync();
        }
        var store = new PostgresOutboxStore(new TestContextFactory(fixture));
        var first = Assert.Single(await store.ClaimAsync(1, now, TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.True(await store.FailAsync(first, now, "BrokerUnavailable", 2, CancellationToken.None));
        Assert.Empty(await store.ClaimAsync(1, now.AddMilliseconds(500), TimeSpan.FromSeconds(10), CancellationToken.None));
        var second = Assert.Single(await store.ClaimAsync(1, now.AddSeconds(3), TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.True(await store.FailAsync(second, now.AddSeconds(3), "BrokerUnavailable", 2, CancellationToken.None));
        Assert.Empty(await store.ClaimAsync(1, now.AddDays(1), TimeSpan.FromSeconds(10), CancellationToken.None));
        await using var verify = fixture.CreateContext();
        var row = await verify.OutboxMessages.SingleAsync(x => x.Id == id);
        Assert.Equal(2, row.Attempts);
        Assert.NotNull(row.DeadLetteredAtUtc);
        Assert.Null(row.PublishedAtUtc);
        Assert.Equal("BrokerUnavailable", row.LastErrorCode);
    }
}

internal sealed class TestContextFactory(PostgresFixture fixture) : IDbContextFactory<OrderFleet.Infrastructure.Persistence.OrderFleetDbContext>
{
    public OrderFleet.Infrastructure.Persistence.OrderFleetDbContext CreateDbContext() => fixture.CreateContext();
}
