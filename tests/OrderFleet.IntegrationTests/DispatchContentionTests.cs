using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using OrderFleet.Application.Dispatch;
using OrderFleet.Domain.Couriers;
using OrderFleet.Domain.Orders;
using OrderFleet.Infrastructure.Persistence;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;
using static OrderFleet.IntegrationTests.DispatchConcurrencyTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class DispatchContentionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task TwoDatabaseSessions_AreProvablyBlockedTogether_BeforeCompetingForOneCourier()
    {
        var sessionName = "dispatch-contention-" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = sessionName }.ConnectionString;
        await using var factory = new ApiFactory(fixture);
        factory.CustomizeServices = services =>
        {
            services.RemoveAll<IDbContextFactory<OrderFleetDbContext>>();
            services.AddSingleton<IDbContextFactory<OrderFleetDbContext>>(new NamedContexts(connection));
        };
        using var courier = await CourierAsync(factory, 15);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, 15);
        var first = await AcceptedAsync(factory, owner, restaurant, product);
        var second = await AcceptedAsync(factory, owner, restaurant, product);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Orders\" WHERE \"Id\" IN ({first}, {second}) ORDER BY \"Id\" FOR UPDATE");
        var contenders = new[] { DispatchAsync(factory, first), DispatchAsync(factory, second) };
        // PostgreSQL activity snapshots can be cached until transaction end.
        // Observe through a separate autocommit connection, not the lock holder.
        await using var observer = fixture.CreateContext();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var waiting = 0;
            while (waiting != 2)
            {
                waiting = await observer.Database.SqlQuery<int>($"""
                    SELECT count(*)::integer AS "Value" FROM pg_stat_activity
                    WHERE application_name = {sessionName} AND wait_event_type = 'Lock' AND state = 'active'
                    """).SingleAsync(deadline.Token);
                if (waiting != 2) await Task.Delay(25, deadline.Token);
            }
            Assert.All(contenders, task => Assert.False(task.IsCompleted));
            await transaction.CommitAsync(deadline.Token);
            var outcomes = await Task.WhenAll(contenders).WaitAsync(deadline.Token);
            Assert.Single(outcomes, x => x == DispatchOutcome.Assigned);
            Assert.Single(outcomes, x => x == DispatchOutcome.NoCourier);
        }
        finally
        {
            if (blocker.Database.CurrentTransaction is not null) await transaction.RollbackAsync();
            // Always observe outstanding tasks after releasing the test-only lock.
            await Task.WhenAll(contenders);
        }
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.Orders.CountAsync(x => (x.Id == first || x.Id == second) && x.Status == OrderStatus.Assigned));
        Assert.Equal(1, await verify.Couriers.CountAsync(x => x.Status == CourierStatus.Reserved));
        Assert.Equal(1, await verify.CourierAssignments.CountAsync(x => x.OrderId == first || x.OrderId == second));
    }
    private sealed class NamedContexts(string connection) : IDbContextFactory<OrderFleetDbContext>
    {
        public OrderFleetDbContext CreateDbContext() => new(new DbContextOptionsBuilder<OrderFleetDbContext>()
            .UseNpgsql(connection, postgres => postgres.UseNetTopologySuite()).Options);
    }
}
