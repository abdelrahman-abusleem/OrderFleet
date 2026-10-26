using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderFleet.Domain.Orders;
using OrderFleet.Infrastructure.Persistence;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class MissingCourierWarningTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneTransition_StillPersists_AndWarnsWithOnlyOrderId(bool delivered)
    {
        var now = DateTimeOffset.UtcNow;
        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), [new(Guid.NewGuid(), "Tea", 1m, 1)], new(10, 10, 10m, 100m), now);
        var courier = Guid.NewGuid();
        order.Accept(now); var assignment = order.Assign(courier, now);
        await using var context = fixture.CreateContext();
        context.Orders.Add(order);
        var logger = new CapturedLogger();
        var store = new EfOrderingStore(context, logger);
        await store.SaveAsync(CancellationToken.None);
        if (delivered) { order.MarkPickedUp(courier, assignment, now); order.Deliver(courier, assignment, now); }
        else order.ReleaseAssignment(assignment, now);
        await store.SaveAsync(CancellationToken.None);
        var warning = Assert.Single(logger.Warnings);
        Assert.Equal(order.Id, warning["OrderId"]);
        Assert.Equal("Courier capacity missing for order {OrderId}", warning["{OriginalFormat}"]);
        Assert.Equal(2, warning.Count);
        await using var verify = fixture.CreateContext();
        Assert.Equal(delivered ? OrderStatus.Delivered : OrderStatus.Accepted, (await verify.Orders.SingleAsync(x => x.Id == order.Id)).Status);
        Assert.False(await verify.Couriers.AnyAsync(x => x.Id == courier));
    }

    private sealed class CapturedLogger : ILogger<EfOrderingStore>
    {
        internal List<Dictionary<string, object?>> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(error);
            if (level == LogLevel.Warning) Warnings.Add(Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state).ToDictionary());
        }
    }
}
