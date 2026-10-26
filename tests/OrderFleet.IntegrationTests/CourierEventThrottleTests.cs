using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderFleet.Application.Dispatch;
using OrderFleet.Infrastructure.Clock;
using OrderFleet.Infrastructure.Persistence;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;
using static OrderFleet.IntegrationTests.DispatchConcurrencyTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class CourierEventThrottleTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(99_999_990L, 0)]
    [InlineData(100_000_000L, 1)]
    [InlineData(100_000_010L, 1)]
    public async Task IntervalBoundary_UsesLastEmittedEvent_WhileEveryObservationPersists(long ticks, int emitted)
    {
        var clock = Clock();
        await using var factory = new ApiFactory(fixture, clock);
        var (client, id, _) = await AssignedAsync(factory);
        using var courier = client;
        clock.Advance(TimeSpan.FromSeconds(1));
        await LocationAsync(courier, 80, clock);
        var count = await CountAsync(id);
        clock.Advance(TimeSpan.FromTicks(ticks));
        await LocationAsync(courier, 80, clock);
        Assert.Equal(count + emitted, await CountAsync(id));
        await AssertProjectionAsync(id, 80, clock.GetUtcNow());
    }

    [Theory]
    [InlineData(0.000001, 0)]
    [InlineData(0, 1)]
    [InlineData(-0.000001, 1)]
    public async Task MovementBoundary_UsesPostGisMetresIncludingEquality(double thresholdOffset, int emitted)
    {
        await using var measure = fixture.CreateContext();
        var distance = await measure.Database.SqlQueryRaw<double>("""
            SELECT ST_Distance(ST_SetSRID(ST_MakePoint(46.7,80),4326)::geography,
                ST_SetSRID(ST_MakePoint(46.7,80.001),4326)::geography) AS "Value"
            """).SingleAsync();
        var clock = Clock();
        await using var factory = new ApiFactory(fixture, clock);
        factory.Overrides["CourierLocationEvents:MinimumDistanceMeters"] = (distance + thresholdOffset).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var (client, id, _) = await AssignedAsync(factory);
        using var courier = client;
        clock.Advance(TimeSpan.FromSeconds(1));
        await LocationAsync(courier, 80, clock);
        var count = await CountAsync(id);
        clock.Advance(TimeSpan.FromSeconds(1));
        await LocationAsync(courier, 80.001m, clock);
        Assert.Equal(count + emitted, await CountAsync(id));
        await AssertProjectionAsync(id, 80.001m, clock.GetUtcNow());
    }

    [Fact]
    public async Task ReleaseSuppressesEvents_AndNewAssignmentEmitsItsFirstObservation()
    {
        var clock = Clock();
        await using var factory = new ApiFactory(fixture, clock);
        var (client, id, orderId) = await AssignedAsync(factory);
        using var courier = client;
        clock.Advance(TimeSpan.FromSeconds(1));
        await LocationAsync(courier, 80, clock);
        var count = await CountAsync(id);
        await using (var context = fixture.CreateContext())
        {
            var order = await context.Orders.SingleAsync(x => x.Id == orderId);
            Assert.Equal(HttpStatusCode.NoContent, (await courier.PostAsJsonAsync($"/api/v1/orders/{orderId}/release-assignment", new { assignmentId = order.AssignmentId })).StatusCode);
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        await LocationAsync(courier, 80, clock);
        Assert.Equal(count, await CountAsync(id));
        Assert.Equal(DispatchOutcome.Assigned, await DispatchAsync(factory, orderId));
        clock.Advance(TimeSpan.FromSeconds(1));
        await LocationAsync(courier, 80, clock);
        Assert.Equal(count + 1, await CountAsync(id));
    }

    [Fact]
    public async Task StaleWriter_RollsBackProjectionEventAndThrottleSnapshotTogether()
    {
        var clock = Clock();
        await using var factory = new ApiFactory(fixture, clock);
        var (client, id, _) = await AssignedAsync(factory);
        using var courier = client;
        clock.Advance(TimeSpan.FromSeconds(1));
        await LocationAsync(courier, 80, clock);
        var count = await CountAsync(id);
        await using var first = fixture.CreateContext();
        await using var stale = fixture.CreateContext();
        var winner = await first.Couriers.SingleAsync(x => x.Id == id);
        var loser = await stale.Couriers.SingleAsync(x => x.Id == id);
        clock.Advance(TimeSpan.FromSeconds(1));
        winner.UpdateLocation(new(80.001m, 46.7m), clock.GetUtcNow());
        await new EfCourierStore(first, new SystemClock(clock)).SaveAsync(winner, CancellationToken.None);
        loser.UpdateLocation(new(80.002m, 46.7m), clock.GetUtcNow().AddSeconds(1));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => new EfCourierStore(stale, new SystemClock(clock)).SaveAsync(loser, CancellationToken.None));
        await AssertProjectionAsync(id, 80.001m, clock.GetUtcNow());
        Assert.Equal(count + 1, await CountAsync(id));
        clock.Advance(TimeSpan.FromSeconds(1));
        await LocationAsync(courier, 80.001001m, clock);
        Assert.Equal(count + 1, await CountAsync(id));
        await AssertProjectionAsync(id, 80.001001m, clock.GetUtcNow());
    }

    [Theory]
    [InlineData("MinimumDistanceMeters", "0")]
    [InlineData("MinimumDistanceMeters", "NaN")]
    [InlineData("MinimumIntervalSeconds", "0")]
    public async Task InvalidThresholds_FailStartup(string name, string value)
    {
        await using var factory = new ApiFactory(fixture);
        factory.Overrides["CourierLocationEvents:" + name] = value;
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    private async Task<(HttpClient Client, Guid Id, Guid OrderId)> AssignedAsync(ApiFactory factory)
    {
        var courier = await CourierAsync(factory, 80);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, 80);
        var order = await AcceptedAsync(factory, owner, restaurant, product);
        Assert.Equal(DispatchOutcome.Assigned, await DispatchAsync(factory, order));
        await using var context = fixture.CreateContext();
        return (courier, (await context.Orders.SingleAsync(x => x.Id == order)).CourierId!.Value, order);
    }
    private static TestClock Clock()
    {
        var instant = DateTimeOffset.UtcNow;
        return new(new(instant.Ticks - instant.Ticks % 10, TimeSpan.Zero));
    }
    private static async Task LocationAsync(HttpClient client, decimal latitude, TestClock clock) =>
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/v1/couriers/me/location",
            new { latitude, longitude = 46.7m, observedAt = clock.GetUtcNow() })).StatusCode);
    private async Task<int> CountAsync(Guid id)
    {
        await using var context = fixture.CreateContext();
        return await context.OutboxMessages.CountAsync(x => x.AggregateId == id && x.Type == "courier.location-updated.v1");
    }
    private async Task AssertProjectionAsync(Guid id, decimal latitude, DateTimeOffset observed)
    {
        await using var context = fixture.CreateContext();
        var profile = await context.Couriers.SingleAsync(x => x.Id == id);
        var position = await context.CourierPositions.SingleAsync(x => x.CourierId == id);
        Assert.Equal(latitude, profile.Latitude);
        Assert.Equal((double)latitude, position.Location.Y);
        Assert.Equal(46.7d, position.Location.X);
        Assert.Equal(observed, profile.LocationObservedAt);
        Assert.Equal(profile.LocationObservedAt, position.ObservedAt);
    }
}
