using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrderFleet.Application.Dispatch;
using OrderFleet.Domain.Orders;
using OrderFleet.Infrastructure.Persistence;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;
using static OrderFleet.IntegrationTests.DispatchConcurrencyTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class OrderTransitionQueryOrderTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("Release", 61)]
    [InlineData("Cancel", 62)]
    [InlineData("Deliver", 63)]
    public async Task Transition_ReadsCourierThenDispatchJobThenAssignmentHistory(string action, int latitude)
    {
        await using var factory = new ApiFactory(fixture);
        using var courier = await CourierAsync(factory, latitude);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, latitude);
        var id = await AcceptedAsync(factory, owner, restaurant, product);
        Assert.Equal(DispatchOutcome.Assigned, await DispatchAsync(factory, id));
        Guid courierId, assignment;
        await using (var before = fixture.CreateContext())
        {
            var order = await before.Orders.SingleAsync(x => x.Id == id);
            courierId = order.CourierId!.Value; assignment = order.AssignmentId!.Value;
        }
        if (action == "Deliver") Assert.Equal(HttpStatusCode.NoContent,
            (await courier.PostAsJsonAsync($"/api/v1/orders/{id}/pickup", new { assignmentId = assignment })).StatusCode);
        var recorder = new QueryOrder();
        await using var context = new OrderFleetDbContext(new DbContextOptionsBuilder<OrderFleetDbContext>()
            .UseNpgsql(fixture.ConnectionString, p => p.UseNetTopologySuite()).AddInterceptors(recorder).Options);
        var aggregate = await context.Orders.SingleAsync(x => x.Id == id);
        var now = DateTimeOffset.UtcNow;
        switch (action)
        {
            case "Release": aggregate.ReleaseAssignment(assignment, now); break;
            case "Cancel": aggregate.Cancel(CancellationReason.AdminCancelled, now); break;
            case "Deliver": aggregate.Deliver(courierId, assignment, now); break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
        Assert.Single(aggregate.DomainEvents);
        recorder.Tables.Clear();
        await new EfOrderingStore(context).SaveAsync(CancellationToken.None);
        Assert.Equal(new[] { "Couriers", "DispatchJobs", "CourierAssignments" }, recorder.Tables);
        Assert.Empty(aggregate.DomainEvents);
    }

    private sealed class QueryOrder : DbCommandInterceptor
    {
        internal List<string> Tables { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal))
                foreach (var table in new[] { "Couriers", "DispatchJobs", "CourierAssignments" })
                    if (command.CommandText.Contains("FROM \"" + table + "\"", StringComparison.Ordinal)) Tables.Add(table);
            return ValueTask.FromResult(result);
        }
    }
}
