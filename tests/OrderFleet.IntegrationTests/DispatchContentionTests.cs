using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Net.Http.Json;
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
    public async Task CandidateBecomesUnavailableAfterSnapshot_StillSelectsNextCourier()
    {
        const long gate = 711052026;
        var session = "candidate-snapshot-" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = session }.ConnectionString;
        await using var factory = new ApiFactory(fixture);
        factory.CustomizeServices = services =>
        {
            services.RemoveAll<IDbContextFactory<OrderFleetDbContext>>();
            services.AddSingleton<IDbContextFactory<OrderFleetDbContext>>(new NamedContexts(connection, new CandidateGate()));
        };
        using var nearest = await CourierAsync(factory, 17);
        using var next = await CourierAsync(factory, 17.001m);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, 17);
        var order = await AcceptedAsync(factory, owner, restaurant, product);
        await using var blocker = fixture.CreateContext();
        await blocker.Database.OpenConnectionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock({gate})");
        var dispatch = DispatchAsync(factory, order);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await using var observer = fixture.CreateContext();
            while (!await observer.Database.SqlQuery<bool>($"""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name = {session}
                    AND wait_event_type = 'Lock' AND state = 'active') AS "Value"
                """).SingleAsync(timeout.Token)) await Task.Delay(20, timeout.Token);
            Assert.False(dispatch.IsCompleted);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await nearest.PutAsJsonAsync(
                "/api/v1/couriers/me/availability", new { isAvailable = false }, timeout.Token)).StatusCode);
        }
        finally { await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({gate})"); }
        Assert.Equal(DispatchOutcome.Assigned, await dispatch.WaitAsync(timeout.Token));
        await using var verify = fixture.CreateContext();
        var courierId = await verify.Couriers.Where(x => x.Latitude == 17.001m).Select(x => x.Id).SingleAsync(timeout.Token);
        Assert.Equal(courierId, (await verify.Orders.SingleAsync(x => x.Id == order, timeout.Token)).CourierId);
    }

    [Fact]
    public async Task LockedNearestCourier_DoesNotHideAnotherEligibleCourier()
    {
        await using var factory = new ApiFactory(fixture);
        using var nearest = await CourierAsync(factory, 16);
        using var next = await CourierAsync(factory, 16.001m);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, 16);
        var orderId = await AcceptedAsync(factory, owner, restaurant, product);
        await using var blocker = fixture.CreateContext();
        var nearestId = await blocker.Couriers.Where(x => x.Latitude == 16).Select(x => x.Id).SingleAsync();
        var nextId = await blocker.Couriers.Where(x => x.Latitude == 16.001m).Select(x => x.Id).SingleAsync();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Couriers\" WHERE \"Id\" = {nearestId} FOR UPDATE");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var outcome = await DispatchAsync(factory, orderId).WaitAsync(timeout.Token);
        Assert.Equal(DispatchOutcome.Assigned, outcome);
        await using var verify = fixture.CreateContext();
        var order = await verify.Orders.SingleAsync(x => x.Id == orderId, timeout.Token);
        Assert.Equal(nextId, order.CourierId);
        Assert.Equal(await verify.Couriers.Where(x => x.Id == nextId).Select(x => x.AssignmentId).SingleAsync(timeout.Token), order.AssignmentId);
        Assert.Null((await verify.Couriers.SingleAsync(x => x.Id == nearestId, timeout.Token)).AssignmentId);
        Assert.Single(await verify.CourierAssignments.Where(x => x.OrderId == orderId).ToArrayAsync(timeout.Token));
        await transaction.RollbackAsync(timeout.Token);
    }

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
    private sealed class NamedContexts(string connection, DbCommandInterceptor? interceptor = null) : IDbContextFactory<OrderFleetDbContext>
    {
        public OrderFleetDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<OrderFleetDbContext>().UseNpgsql(connection, postgres => postgres.UseNetTopologySuite());
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            return new(builder.Options);
        }
    }
    private sealed class CandidateGate : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            command.CommandText = command.CommandText.Replace("JOIN \"CourierPositions\" p", "JOIN LATERAL (SELECT pg_advisory_xact_lock(711052026)) gate ON true JOIN \"CourierPositions\" p", StringComparison.Ordinal);
            return ValueTask.FromResult(result);
        }
    }
}
