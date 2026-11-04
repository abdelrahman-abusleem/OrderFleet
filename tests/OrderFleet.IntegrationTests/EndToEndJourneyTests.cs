using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.Internal;
using OrderFleet.Api.Development;
using OrderFleet.Application.Realtime;
using OrderFleet.Domain.Couriers;
using OrderFleet.Domain.Orders;
using OrderFleet.Domain.Payments;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "EndToEnd")]
public sealed class EndToEndJourneyTests(PostgresFixture postgres, RabbitFixture rabbit)
    : IClassFixture<PostgresFixture>, IClassFixture<RabbitFixture>
{
    [Fact]
    public async Task RealInfrastructureJourney_SurvivesHostRestart_AndDeliversOnePaidPrivateOrder()
    {
        var prefix = "journey-" + Guid.NewGuid().ToString("N");
        var factory = Host(prefix);
        try
        {
            var password = "Aa1!" + Guid.NewGuid().ToString("N");
            var manifest = await DemoBootstrap.ExecuteAsync(factory.Services,
                new HostingEnvironment { EnvironmentName = "Development" }, password, CancellationToken.None);
            using var customer = await LoginAsync(factory, "customer", password);
            using var owner = await LoginAsync(factory, "owner", password);
            using var courier = await LoginAsync(factory, "courier", password);
            using var stranger = await LoginAsync(factory, "stranger", password);
            Assert.Equal(HttpStatusCode.NoContent, (await courier.PutAsJsonAsync("/api/v1/couriers/me/availability", new { isAvailable = true })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await courier.PutAsJsonAsync("/api/v1/couriers/me/location",
                new { latitude = 24.7136m, longitude = 46.6753m, observedAt = DateTimeOffset.UtcNow })).StatusCode);
            var body = new { restaurantId = manifest.RestaurantId, items = new[] { new { productId = manifest.ProductId, quantity = 2 } }, latitude = 24.72m, longitude = 46.68m };
            var key = Guid.NewGuid().ToString("N");
            var created = await CreateAsync(customer, body, key);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            var replay = await CreateAsync(customer, body, key);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(id, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/orders/{id}/delivery")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsync($"/api/v1/orders/{id}/accept", null)).StatusCode);
            var initial = await customer.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}");
            Assert.Equal(51m, initial.GetProperty("totalAmount").GetDecimal());
            Assert.Equal("SAR", initial.GetProperty("currency").GetString());
            Assert.False(initial.TryGetProperty("latitude", out _));
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/orders/{id}/accept", null)).StatusCode);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Guid assignment;
            while (true)
            {
                await using var context = postgres.CreateContext();
                var order = await context.Orders.SingleAsync(x => x.Id == id, timeout.Token);
                if (order.Status == OrderStatus.Assigned) { assignment = order.AssignmentId!.Value; break; }
                await Task.Delay(50, timeout.Token);
            }
            var eta = await customer.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}/eta");
            Assert.True(eta.GetProperty("isApproximate").GetBoolean());
            Assert.Equal("Approximate", eta.GetProperty("confidence").GetString());
            var signingKey = factory.SigningKey;
            var token = customer.DefaultRequestHeaders.Authorization!.Parameter!;
            var courierToken = courier.DefaultRequestHeaders.Authorization!.Parameter!;
            await factory.DisposeAsync();
            factory = Host(prefix, signingKey);
            using var resumed = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
            resumed.DefaultRequestHeaders.Authorization = new("Bearer", courierToken);
            var afterRestart = await resumed.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}");
            Assert.Equal(assignment, afterRestart.GetProperty("assignmentId").GetGuid());
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => DispatchConcurrencyTests.DispatchAsync(factory, id)));
            await using var hub = RealtimeApiTests.Connection(factory, token);
            await hub.StartAsync(timeout.Token);
            var snapshot = await hub.InvokeAsync<OrderSubscriptionSnapshot>("SubscribeOrder", id, timeout.Token);
            Assert.Equal("Assigned", snapshot.Order.Status);
            var delivered = new TaskCompletionSource<OrderUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            hub.On<OrderUpdate>("OrderUpdated", update => { if (update.OrderId == id && update.Status == "Delivered") delivered.TrySetResult(update); });
            Assert.Equal(HttpStatusCode.NoContent, (await resumed.PostAsJsonAsync($"/api/v1/orders/{id}/pickup", new { assignmentId = assignment })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await resumed.PostAsJsonAsync($"/api/v1/orders/{id}/deliver", new { assignmentId = assignment })).StatusCode);
            var update = await delivered.Task.WaitAsync(timeout.Token);
            Assert.True(update.Sequence > snapshot.Order.Sequence);
            Assert.Equal(HttpStatusCode.Conflict, (await resumed.PostAsJsonAsync($"/api/v1/orders/{id}/deliver", new { assignmentId = assignment })).StatusCode);
            await using var verify = postgres.CreateContext();
            var stored = await verify.Orders.SingleAsync(x => x.Id == id, timeout.Token);
            var payment = await verify.Payments.SingleAsync(x => x.OrderId == id, timeout.Token);
            Assert.Equal(OrderStatus.Delivered, stored.Status);
            Assert.Equal(PaymentState.Captured, payment.State);
            Assert.Equal(1, await verify.MockPaymentEffects.CountAsync(x => x.AuthorizationId == payment.AuthorizationId && x.Operation == "Capture", timeout.Token));
            Assert.Equal(1, await verify.CourierAssignments.CountAsync(x => x.OrderId == id, timeout.Token));
            var profile = await verify.Couriers.SingleAsync(x => x.Id == stored.CourierId, timeout.Token);
            Assert.Equal(CourierStatus.Available, profile.Status);
            Assert.Null(profile.AssignmentId);


            while (!await verify.OutboxMessages.AnyAsync(x => x.AggregateId == id && x.Type == "order.delivered.v1" && x.PublishedAtUtc != null, timeout.Token))
                await Task.Delay(50, timeout.Token);
        }
        finally { await factory.DisposeAsync(); }
    }

    private ApiFactory Host(string prefix, string? signingKey = null)
    {
        var host = new ApiFactory(postgres, signingKey: signingKey);
        host.Overrides["Messaging:Enabled"] = "true";
        host.Overrides["Messaging:Uri"] = rabbit.ConnectionString;
        host.Overrides["Messaging:Prefix"] = prefix;
        host.Overrides["Recovery:Enabled"] = "true";
        return host;
    }
    private static async Task<HttpClient> LoginAsync(ApiFactory factory, string name, string password)
    {
        var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var result = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = name + "@demo.orderfleet.test", password });
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var token = (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
