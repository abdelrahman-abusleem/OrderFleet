using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderFleet.Application.Dispatch;
using OrderFleet.Domain.Couriers;
using OrderFleet.Domain.Orders;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class DispatchConcurrencyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Dispatch_RefusesAcceptedOrderWithoutCapturedPayment()
    {
        await using var factory = new ApiFactory(fixture);
        using var courier = await CourierAsync(factory, 60);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, 60);
        using var customer = await ClientAsync(factory, "Customer");
        var created = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();


        await using (var context = fixture.CreateContext())
        {
            var order = await context.Orders.SingleAsync(x => x.Id == id);
            order.Accept(DateTimeOffset.UtcNow);
            await new OrderFleet.Infrastructure.Persistence.EfOrderingStore(context).SaveAsync(CancellationToken.None);
        }
        Assert.Equal(DispatchOutcome.NotEligible, await DispatchAsync(factory, id));
        await using var verify = fixture.CreateContext();
        Assert.Equal(OrderStatus.Accepted, (await verify.Orders.SingleAsync(x => x.Id == id)).Status);
        Assert.Equal(CourierStatus.Available, (await verify.Couriers.SingleAsync(x => x.Latitude == 60)).Status);
        Assert.False(await verify.DispatchAttempts.AnyAsync(x => x.OrderId == id));
    }

    [Fact]
    public async Task Database_RejectsTwoActiveOrdersWithSameCourier_WithoutChangingWinner()
    {
        var courier = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Order Build()
        {
            var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), [new(Guid.NewGuid(), "Tea", 1m, 1)], new(10, 10, 100m, 1000m), now);
            order.Accept(now);
            order.Assign(courier, now);
            return order;
        }
        var winner = Build();
        var loser = Build();
        await using (var context = fixture.CreateContext())
        {
            context.Orders.Add(winner);
            await context.SaveChangesAsync();
        }
        await using (var context = fixture.CreateContext())
        {
            context.Orders.Add(loser);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            var postgres = Assert.IsType<Npgsql.PostgresException>(error.InnerException);
            Assert.Equal("IX_Orders_CourierId", postgres.ConstraintName);
        }
        await using var verify = fixture.CreateContext();
        Assert.True(await verify.Orders.AnyAsync(x => x.Id == winner.Id && x.Status == OrderStatus.Assigned));
        Assert.False(await verify.Orders.AnyAsync(x => x.Id == loser.Id));
    }

    [Fact]
    public async Task TwoWorkersAndTwoCouriers_AssignOrderOnce_LeavingOtherCapacityAvailable()
    {
        await using var factory = new ApiFactory(fixture);
        using var first = await CourierAsync(factory, 30);
        using var second = await CourierAsync(factory, 30);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, 30);
        var order = await AcceptedAsync(factory, owner, restaurant, product);
        var outcomes = await Task.WhenAll(DispatchAsync(factory, order), DispatchAsync(factory, order));
        Assert.Single(outcomes, x => x == DispatchOutcome.Assigned);
        Assert.Single(outcomes, x => x == DispatchOutcome.NotEligible);
        await using var verify = fixture.CreateContext();
        var profiles = await verify.Couriers.Where(x => x.Latitude == 30).ToArrayAsync();
        Assert.Single(profiles, x => x.Status == CourierStatus.Reserved && x.OrderId == order);
        Assert.Single(profiles, x => x.Status == CourierStatus.Available && x.OrderId == null);
        Assert.Equal(1, await verify.DispatchAttempts.CountAsync(x => x.OrderId == order && x.Outcome == "Assigned"));
    }

    [Fact]
    public async Task AdminCancellationRacingDispatch_LeavesNoLeakedCourierReservation()
    {
        await using var factory = new ApiFactory(fixture);
        using var courier = await CourierAsync(factory, 40);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        using var admin = await ClientAsync(factory, "Admin");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, 40);
        var orderId = await AcceptedAsync(factory, owner, restaurant, product);
        var dispatch = DispatchAsync(factory, orderId);
        var cancel = admin.PostAsync($"/api/v1/orders/{orderId}/cancel", null);
        await Task.WhenAll(dispatch, cancel);
        var cancellation = await cancel;
        if (cancellation.StatusCode == HttpStatusCode.Conflict)
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/orders/{orderId}/cancel", null)).StatusCode);
        else Assert.Equal(HttpStatusCode.NoContent, cancellation.StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(OrderStatus.Cancelled, (await verify.Orders.SingleAsync(x => x.Id == orderId)).Status);
        var profile = await verify.Couriers.SingleAsync(x => x.Latitude == 40);
        Assert.Equal(CourierStatus.Available, profile.Status);
        Assert.Null(profile.OrderId);
        Assert.Null(profile.AssignmentId);
    }

    [Fact]
    public async Task PickupRacingRelease_CommitsOneLegalStateAndMatchingCapacity()
    {
        await using var factory = new ApiFactory(fixture);
        using var courier = await CourierAsync(factory, 50);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, 50);
        var id = await AcceptedAsync(factory, owner, restaurant, product);
        Assert.Equal(DispatchOutcome.Assigned, await DispatchAsync(factory, id));
        var assignment = (await courier.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}")).GetProperty("assignmentId").GetGuid();
        var pickup = courier.PostAsJsonAsync($"/api/v1/orders/{id}/pickup", new { assignmentId = assignment });
        var release = courier.PostAsJsonAsync($"/api/v1/orders/{id}/release-assignment", new { assignmentId = assignment });
        await Task.WhenAll(pickup, release);
        var pickedUp = await pickup;
        var released = await release;
        await using var verify = fixture.CreateContext();
        var order = await verify.Orders.SingleAsync(x => x.Id == id);
        var profile = await verify.Couriers.SingleAsync(x => x.Latitude == 50);
        if (order.Status == OrderStatus.PickedUp)
        {
            Assert.Equal(HttpStatusCode.NoContent, pickedUp.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, released.StatusCode);
            Assert.Equal(CourierStatus.Busy, profile.Status);
            Assert.Equal(assignment, profile.AssignmentId);
        }
        else
        {
            Assert.Equal(OrderStatus.Accepted, order.Status);
            Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, pickedUp.StatusCode);
            Assert.Equal(CourierStatus.Available, profile.Status);
            Assert.Null(profile.AssignmentId);
        }
    }

    [Fact]
    public async Task TwoOrdersCompetingForOneCourier_ReserveOnlyOneCapacity()
    {
        await using var factory = new ApiFactory(fixture);
        using var courier = await CourierAsync(factory, 10);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, 10);
        var first = await AcceptedAsync(factory, owner, restaurant, product);
        var second = await AcceptedAsync(factory, owner, restaurant, product);
        var results = await Task.WhenAll(DispatchAsync(factory, first), DispatchAsync(factory, second));
        Assert.Single(results, x => x == DispatchOutcome.Assigned);
        Assert.Single(results, x => x == DispatchOutcome.NoCourier);
        await using var context = fixture.CreateContext();
        var orders = await context.Orders.Where(x => x.Id == first || x.Id == second).ToArrayAsync();
        Assert.Single(orders, x => x.Status == OrderStatus.Assigned);
        Assert.Single(orders, x => x.Status == OrderStatus.Accepted);
        var assigned = orders.Single(x => x.Status == OrderStatus.Assigned);
        var reserved = await context.Couriers.SingleAsync(x => x.Id == assigned.CourierId);
        Assert.Equal(CourierStatus.Reserved, reserved.Status);
        Assert.Equal(assigned.Id, reserved.OrderId);
        Assert.Equal(assigned.AssignmentId, reserved.AssignmentId);
    }

    [Fact]
    public async Task DuplicateDispatch_AndSameCourierReassignment_FenceOldCommands()
    {
        await using var factory = new ApiFactory(fixture);
        using var courier = await CourierAsync(factory, 20);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, 20);
        var id = await AcceptedAsync(factory, owner, restaurant, product);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => DispatchAsync(factory, id)));
        Assert.Single(outcomes, x => x == DispatchOutcome.Assigned);
        var initial = await courier.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}");
        var old = initial.GetProperty("assignmentId").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await courier.PostAsJsonAsync($"/api/v1/orders/{id}/release-assignment", new { assignmentId = old })).StatusCode);
        Assert.Equal(DispatchOutcome.Assigned, await DispatchAsync(factory, id));
        var current = (await courier.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}")).GetProperty("assignmentId").GetGuid();
        Assert.NotEqual(old, current);
        foreach (var action in new[] { "pickup", "deliver", "release-assignment" })
            Assert.Equal(HttpStatusCode.Conflict, (await courier.PostAsJsonAsync($"/api/v1/orders/{id}/{action}", new { assignmentId = old })).StatusCode);
        using var stranger = await ClientAsync(factory, "Courier");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/api/v1/orders/{id}/pickup", new { assignmentId = current })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await courier.PostAsJsonAsync($"/api/v1/orders/{id}/pickup", new { assignmentId = Guid.Empty })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await courier.PostAsJsonAsync($"/api/v1/orders/{id}/pickup", new { assignmentId = current })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await courier.PutAsJsonAsync("/api/v1/couriers/me/availability", new { isAvailable = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await courier.PostAsJsonAsync($"/api/v1/orders/{id}/deliver", new { assignmentId = current })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await courier.PostAsJsonAsync($"/api/v1/orders/{id}/deliver", new { assignmentId = current })).StatusCode);
        await using var verify = fixture.CreateContext();
        var stored = await verify.Orders.SingleAsync(x => x.Id == id);
        var profile = await verify.Couriers.SingleAsync(x => x.Id == stored.CourierId);
        Assert.Equal(OrderStatus.Delivered, stored.Status);
        Assert.Equal(CourierStatus.Available, profile.Status);
        Assert.Null(profile.AssignmentId);
        Assert.Equal(2, await verify.DispatchAttempts.CountAsync(x => x.OrderId == id && x.Outcome == "Assigned"));
    }

    internal static async Task<HttpClient> CourierAsync(ApiFactory factory, decimal latitude = 24.7m)
    {
        var client = await ClientAsync(factory, "Courier");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync("/api/v1/couriers/me", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/v1/couriers/me/availability", new { isAvailable = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/v1/couriers/me/location",
            new { latitude, longitude = 46.7m, observedAt = factory.Services.GetRequiredService<OrderFleet.Application.Clock.IClock>().UtcNow })).StatusCode);
        return client;
    }

    internal static async Task<(Guid Restaurant, Guid Product)> CatalogForDispatchAsync(HttpClient owner, decimal latitude)
    {
        var restaurant = await owner.PostAsJsonAsync("/api/v1/restaurants", new { name = "Dispatch kitchen", latitude, longitude = 46.7m });
        Assert.Equal(HttpStatusCode.Created, restaurant.StatusCode);
        var id = (await restaurant.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var product = await owner.PostAsJsonAsync($"/api/v1/restaurants/{id}/products", new { name = "Tea", price = 3.25m, isAvailable = true });
        Assert.Equal(HttpStatusCode.Created, product.StatusCode);
        return (id, (await product.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    internal static async Task<Guid> AcceptedAsync(ApiFactory factory, HttpClient owner, Guid restaurant, Guid product)
    {
        using var customer = await ClientAsync(factory, "Customer");
        var response = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/orders/{id}/accept", null)).StatusCode);
        return id;
    }

    internal static async Task<DispatchOutcome> DispatchAsync(ApiFactory factory, Guid orderId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DispatchOrder>().ExecuteAsync(orderId, null, CancellationToken.None);
    }
}
