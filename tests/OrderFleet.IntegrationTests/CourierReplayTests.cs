using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OrderFleet.Application.Dispatch;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;
using static OrderFleet.IntegrationTests.DispatchConcurrencyTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class CourierReplayTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [Trait("Phase", "2")]
    [InlineData("Replay", HttpStatusCode.NoContent, 0)]
    [InlineData("Changed", HttpStatusCode.Conflict, 0)]
    [InlineData("Older", HttpStatusCode.Conflict, 0)]
    [InlineData("Newer", HttpStatusCode.NoContent, 1)]
    public async Task LocationReplay_PreservesProjectionAndEmitsOnlyAcceptedChanges(string operation, HttpStatusCode expected, int newEvents)
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new TestClock(new(now.Ticks - now.Ticks % 10 + 7, TimeSpan.Zero));
        await using var factory = new ApiFactory(fixture, clock);
        using var courier = await CourierAsync(factory, 70);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, 70);
        var id = await AcceptedAsync(factory, owner, restaurant, product);
        Assert.Equal(DispatchOutcome.Assigned, await DispatchAsync(factory, id));
        await using var before = fixture.CreateContext();
        var profile = await before.Couriers.SingleAsync(x => x.OrderId == id);
        var eventCount = await before.OutboxMessages.CountAsync(x => x.AggregateId == profile.Id);
        var latitude = operation == "Replay" ? 70m : 70.001m;
        var observed = clock.GetUtcNow().AddSeconds(operation == "Older" ? -1 : operation == "Newer" ? 1 : 0);
        var response = await courier.PutAsJsonAsync("/api/v1/couriers/me/location", new { latitude, longitude = 46.7m, observedAt = observed });
        Assert.Equal(expected, response.StatusCode);
        await using var verify = fixture.CreateContext();
        var stored = await verify.Couriers.SingleAsync(x => x.Id == profile.Id);
        var position = await verify.CourierPositions.SingleAsync(x => x.CourierId == profile.Id);
        Assert.Equal(operation == "Newer" ? latitude : 70m, stored.Latitude);
        Assert.Equal(46.7m, stored.Longitude);
        Assert.Equal((double)stored.Latitude!.Value, position.Location.Y);
        Assert.Equal((double)stored.Longitude!.Value, position.Location.X);
        Assert.Equal(stored.LocationObservedAt, position.ObservedAt);
        Assert.Equal(operation == "Newer" ? profile.LocationObservedAt!.Value.AddSeconds(1) : profile.LocationObservedAt, stored.LocationObservedAt);
        Assert.Equal(eventCount + newEvents, await verify.OutboxMessages.CountAsync(x => x.AggregateId == profile.Id));
    }

    [Fact]
    [Trait("Phase", "2")]
    public async Task UnassignedLocation_PersistsBothRepresentationsWithoutOutboxEvent()
    {
        await using var factory = new ApiFactory(fixture);
        using var client = await CourierAsync(factory, 75);
        await using var verify = fixture.CreateContext();
        var courier = await verify.Couriers.SingleAsync(x => x.Latitude == 75);
        var position = await verify.CourierPositions.SingleAsync(x => x.CourierId == courier.Id);
        Assert.Equal(75d, position.Location.Y);
        Assert.Equal(46.7d, position.Location.X);
        Assert.Equal(courier.LocationObservedAt, position.ObservedAt);
        Assert.Null(courier.AssignmentId);
        Assert.False(await verify.OutboxMessages.AnyAsync(x => x.AggregateId == courier.Id));
    }
}
