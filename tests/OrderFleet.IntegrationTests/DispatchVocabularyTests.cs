using Microsoft.EntityFrameworkCore;
using OrderFleet.Domain.Orders;
using OrderFleet.Infrastructure.Dispatch;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class DispatchVocabularyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(DispatchJobStatuses.Pending, "Pending")]
    [InlineData(DispatchJobStatuses.Assigned, "Assigned")]
    [InlineData(DispatchJobStatuses.Completed, "Completed")]
    [InlineData(DispatchJobStatuses.Exhausted, "Exhausted")]
    public async Task JobStatus_RoundTripsUnchangedText(string value, string expected)
    {
        var now = DateTimeOffset.UtcNow;
        var order = NewOrder(now);
        order.Accept(now);
        if (expected == "Assigned") order.Assign(Guid.NewGuid(), now);
        if (expected == "Completed") order.Cancel(CancellationReason.RestaurantCancelled, now);
        await using var context = fixture.CreateContext();
        context.Orders.Add(order); await context.SaveChangesAsync();


        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "DispatchJobs" ("OrderId", "Attempts", "AssignmentCount", "Status", "NextAttemptAtUtc", "AssignmentId")
            VALUES ({order.Id}, 0, 0, {value}, {now}, {order.AssignmentId})
            """);
        Assert.Equal(expected, (await context.DispatchJobs.AsNoTracking().SingleAsync(x => x.OrderId == order.Id)).Status);
        Assert.Equal(expected, await context.Database.SqlQuery<string>($"SELECT \"Status\" AS \"Value\" FROM \"DispatchJobs\" WHERE \"OrderId\" = {order.Id}").SingleAsync());
    }

    [Theory]
    [InlineData(DispatchAttemptOutcomes.NoCourier, "NoCourier")]
    [InlineData(DispatchAttemptOutcomes.Assigned, "Assigned")]
    [InlineData(DispatchAttemptOutcomes.AssignmentLimit, "AssignmentLimit")]
    public async Task AttemptOutcome_RoundTripsUnchangedText(string value, string expected)
    {
        var now = DateTimeOffset.UtcNow;
        var order = NewOrder(now); order.Accept(now);
        await using var context = fixture.CreateContext();
        context.Orders.Add(order); await context.SaveChangesAsync();
        var id = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "DispatchAttempts" ("Id", "OrderId", "Outcome", "OccurredAtUtc") VALUES ({id}, {order.Id}, {value}, {now})
            """);
        Assert.Equal(expected, (await context.DispatchAttempts.AsNoTracking().SingleAsync(x => x.Id == id)).Outcome);
        Assert.Equal(expected, await context.Database.SqlQuery<string>($"SELECT \"Outcome\" AS \"Value\" FROM \"DispatchAttempts\" WHERE \"Id\" = {id}").SingleAsync());
    }

    private static Order NewOrder(DateTimeOffset now) => Order.Create(Guid.NewGuid(), Guid.NewGuid(),
        [new(Guid.NewGuid(), "Tea", 1m, 1)], new(10, 10, 100m, 1000m), now);
}
