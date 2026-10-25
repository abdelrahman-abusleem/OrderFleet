using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderFleet.Domain.Couriers;
using OrderFleet.Domain.Geo;
using OrderFleet.Infrastructure.Persistence;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class CourierSpatialTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Locations_AreOwnedOrderedAndBounded_AndDiscoveryFiltersStateAndFreshness()
    {
        var initial = DateTimeOffset.UtcNow;
        var clock = new TestClock(new DateTimeOffset(initial.Ticks - initial.Ticks % 10 + 7, TimeSpan.Zero));
        await using var factory = new ApiFactory(fixture, clock);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, _) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PutAsync("/api/v1/couriers/me", null)).StatusCode);
        using var courier = await ClientAsync(factory, "Courier");
        Assert.Equal(HttpStatusCode.NoContent, (await courier.PutAsync("/api/v1/couriers/me", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await courier.PutAsync("/api/v1/couriers/me", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await courier.PutAsJsonAsync("/api/v1/couriers/me/availability", new { isAvailable = true })).StatusCode);
        var now = clock.GetUtcNow();
        Assert.Equal(HttpStatusCode.NoContent, (await LocationAsync(courier, 24.7m, 46.7m, now)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await LocationAsync(courier, 0, 0, now)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await LocationAsync(courier, 0, 0, now.AddSeconds(-1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await LocationAsync(courier, 91, 0, now.AddSeconds(1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await LocationAsync(courier, 0, 0, now.AddSeconds(16))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await LocationAsync(courier, 0, 0, now.AddSeconds(-121))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await courier.PutAsJsonAsync("/api/v1/couriers/me/location", new { latitude = 24, longitude = 46 })).StatusCode);
        var own = await courier.GetFromJsonAsync<JsonElement>("/api/v1/couriers/me");
        var id = own.GetProperty("id").GetGuid();
        using var other = await ClientAsync(factory, "Courier");
        await other.PutAsync("/api/v1/couriers/me", null);
        var nearby = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/restaurants/{restaurant}/nearby-couriers?radiusMeters=100");
        Assert.Single(nearby.EnumerateArray());
        Assert.Equal(id, nearby[0].GetProperty("id").GetGuid());
        Assert.InRange(nearby[0].GetProperty("distanceMeters").GetDouble(), 0, 0.01);
        Assert.False(nearby[0].TryGetProperty("latitude", out _));
        using var stranger = await ClientAsync(factory, "RestaurantOwner");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/restaurants/{restaurant}/nearby-couriers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync($"/api/v1/restaurants/{restaurant}/nearby-couriers")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/api/v1/restaurants/{restaurant}/nearby-couriers?radiusMeters=50001")).StatusCode);
        await courier.PutAsJsonAsync("/api/v1/couriers/me/availability", new { isAvailable = false });
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>($"/api/v1/restaurants/{restaurant}/nearby-couriers")).EnumerateArray());
        await courier.PutAsJsonAsync("/api/v1/couriers/me/availability", new { isAvailable = true });
        clock.Advance(TimeSpan.FromSeconds(121));
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>($"/api/v1/restaurants/{restaurant}/nearby-couriers")).EnumerateArray());
        await using var verify = fixture.CreateContext();
        var stored = await verify.Couriers.SingleAsync(x => x.Id == id);
        var point = await verify.CourierPositions.SingleAsync(x => x.CourierId == id);
        Assert.Equal(24.7m, stored.Latitude);
        Assert.Equal(46.7, point.Location.X);
        Assert.Equal(24.7, point.Location.Y);
        Assert.Equal(4326, point.Location.SRID);
    }

    [Fact]
    public async Task Geography_UsesMetersAcrossDateline_AndBoundaryIsInclusive()
    {
        await using var factory = new ApiFactory(fixture);
        using var courier = await ClientAsync(factory, "Courier");
        await courier.PutAsync("/api/v1/couriers/me", null);
        await courier.PutAsJsonAsync("/api/v1/couriers/me/availability", new { isAvailable = true });
        var now = DateTimeOffset.UtcNow;
        await LocationAsync(courier, 0, 179.999m, now);
        await using var context = fixture.CreateContext();
        var store = new EfCourierStore(context, new OrderFleet.Infrastructure.Clock.SystemClock(TimeProvider.System));
        var results = await store.NearbyAsync(0, -179.999m, 300, now.AddMinutes(-2), now.AddSeconds(15), 50, CancellationToken.None);
        var result = Assert.Single(results);
        Assert.InRange(result.DistanceMeters, 222, 223);
        Assert.Empty(await store.NearbyAsync(0, -179.999m, result.DistanceMeters - 0.01, now.AddMinutes(-2), now.AddSeconds(15), 50, CancellationToken.None));
        Assert.Single(await store.NearbyAsync(0, -179.999m, result.DistanceMeters + 0.01, now.AddMinutes(-2), now.AddSeconds(15), 50, CancellationToken.None));
        Assert.Single(await store.NearbyAsync(0, 179.999m, 0, now, now, 50, CancellationToken.None));
        var index = await context.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'CourierPositions' AND indexname = 'IX_CourierPositions_Location'").SingleAsync();
        Assert.Contains("gist", index, StringComparison.OrdinalIgnoreCase);
        var extension = await context.Database.SqlQueryRaw<string>("SELECT extversion AS \"Value\" FROM pg_extension WHERE extname = 'postgis'").SingleAsync();
        Assert.StartsWith("3.5", extension);
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SET enable_seqscan = off";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "EXPLAIN SELECT * FROM \"CourierPositions\" WHERE ST_DWithin(\"Location\", ST_SetSRID(ST_MakePoint(-179.999, 0),4326)::geography, 300)";
        await using var reader = await command.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(0));
        Assert.Contains(plan, line => line.Contains("IX_CourierPositions_Location", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentLocationUpdates_CannotOverwriteNewerObservation_OrItsSpatialPoint()
    {
        await using var factory = new ApiFactory(fixture);
        using var client = await ClientAsync(factory, "Courier");
        await client.PutAsync("/api/v1/couriers/me", null);
        var id = (await client.GetFromJsonAsync<JsonElement>("/api/v1/couriers/me")).GetProperty("id").GetGuid();
        var now = DateTimeOffset.UtcNow;
        await LocationAsync(client, 24, 46, now);
        await using var stale = fixture.CreateContext();
        await using var fresh = fixture.CreateContext();
        var old = await stale.Couriers.SingleAsync(x => x.Id == id);
        var current = await fresh.Couriers.SingleAsync(x => x.Id == id);
        current.UpdateLocation(new(25, 47), now.AddSeconds(2));
        await new EfCourierStore(fresh, new OrderFleet.Infrastructure.Clock.SystemClock(TimeProvider.System)).SaveAsync(current, CancellationToken.None);
        old.UpdateLocation(new(0, 0), now.AddSeconds(1));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => new EfCourierStore(stale, new OrderFleet.Infrastructure.Clock.SystemClock(TimeProvider.System)).SaveAsync(old, CancellationToken.None));
        await using var verify = fixture.CreateContext();
        Assert.Equal(25m, (await verify.Couriers.SingleAsync(x => x.Id == id)).Latitude);
        Assert.Equal(47, (await verify.CourierPositions.SingleAsync(x => x.CourierId == id)).Location.X);
    }

    private static Task<HttpResponseMessage> LocationAsync(HttpClient client, decimal latitude, decimal longitude, DateTimeOffset observedAt) =>
        client.PutAsJsonAsync("/api/v1/couriers/me/location", new { latitude, longitude, observedAt });
}
