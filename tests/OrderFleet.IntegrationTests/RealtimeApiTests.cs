using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Net.WebSockets;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderFleet.Application.Realtime;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;
using static OrderFleet.IntegrationTests.DispatchConcurrencyTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class RealtimeApiTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task BrowserWebSocketToken_WorksForHub_AndIsRejectedOnOtherApiPaths()
    {
        await using var factory = new ApiFactory(fixture);
        using var customer = await ClientAsync(factory, "Customer");
        var token = customer.DefaultRequestHeaders.Authorization!.Parameter!;
        using var unauthenticated = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await unauthenticated.GetAsync("/api/v1/auth/me?access_token=" + Uri.EscapeDataString(token))).StatusCode);
        var client = factory.Server.CreateWebSocketClient();
        using var socket = await client.ConnectAsync(new Uri("wss://localhost/hubs/orders?access_token=" + Uri.EscapeDataString(token)), CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[4096];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handshake = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Contains("{}", Encoding.UTF8.GetString(buffer, 0, handshake.Count));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);
    }

    [Fact]
    public async Task HubConnection_ClosesWhenItsAccessTokenExpires()
    {
        await using var factory = new ApiFactory(fixture);
        using var customer = await ClientAsync(factory, "Customer");
        var original = new JwtSecurityTokenHandler().ReadJwtToken(customer.DefaultRequestHeaders.Authorization!.Parameter);
        var now = DateTimeOffset.UtcNow;
        var shortToken = new JwtSecurityToken(ApiFactory.Issuer, ApiFactory.Audience,
            original.Claims.Where(x => x.Type is "sub" or "role"), now.UtcDateTime, now.AddSeconds(4).UtcDateTime,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(factory.SigningKey)), SecurityAlgorithms.HmacSha256));
        await using var connection = Connection(factory, new JwtSecurityTokenHandler().WriteToken(shortToken));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await connection.StartAsync();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task Hub_RequiresAuthenticationAndOrderOwnership_AndRefreshesSnapshotOnReconnect()
    {
        await using var factory = new ApiFactory(fixture);
        await using var anonymous = Connection(factory, null);
        await Assert.ThrowsAsync<HttpRequestException>(() => anonymous.StartAsync());
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        using var stranger = await ClientAsync(factory, "Customer");
        var created = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var other = Connection(factory, stranger.DefaultRequestHeaders.Authorization!.Parameter);
        await other.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => other.InvokeAsync<OrderSubscriptionSnapshot>("SubscribeOrder", id));
        await using var connection = Connection(factory, customer.DefaultRequestHeaders.Authorization!.Parameter);
        await connection.StartAsync();
        var initial = await connection.InvokeAsync<OrderSubscriptionSnapshot>("SubscribeOrder", id);
        Assert.Equal("Created", initial.Order.Status);
        Assert.Equal(1, initial.Order.Sequence);
        Assert.Null(initial.CourierLocation);
        var updates = Channel.CreateUnbounded<OrderUpdate>();
        connection.On<OrderUpdate>("OrderUpdated", update => updates.Writer.TryWrite(update));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/orders/{id}/accept", null)).StatusCode);
        await PublishCommittedAsync(factory, id, "order.accepted.v1");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepted = await updates.Reader.ReadAsync(timeout.Token);
        Assert.Equal("Accepted", accepted.Status);
        Assert.Equal(2, accepted.Sequence);
        // An older persisted fact must not regress the connected client's snapshot.
        await PublishCommittedAsync(factory, id, "order.created.v1");
        Assert.False(updates.Reader.TryRead(out _));
        await connection.StopAsync();
        await connection.StartAsync();
        var restored = await connection.InvokeAsync<OrderSubscriptionSnapshot>("SubscribeOrder", id);
        Assert.Equal(accepted, restored.Order);
    }

    [Fact]
    public async Task MembershipAndRoleRevocation_AreRecheckedBeforeNotification()
    {
        await using var factory = new ApiFactory(fixture);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        var created = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var connection = Connection(factory, owner.DefaultRequestHeaders.Authorization!.Parameter);
        await connection.StartAsync();
        await connection.InvokeAsync<OrderSubscriptionSnapshot>("SubscribeOrder", id);
        var updates = Channel.CreateUnbounded<OrderUpdate>();
        connection.On<OrderUpdate>("OrderUpdated", update => updates.Writer.TryWrite(update));
        await using (var context = fixture.CreateContext())
        {
            var membership = await context.RestaurantMemberships.SingleAsync(x => x.RestaurantId == restaurant);
            context.RestaurantMemberships.Remove(membership);
            await context.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NoContent, (await customer.PostAsync($"/api/v1/orders/{id}/cancel", null)).StatusCode);
        await PublishCommittedAsync(factory, id, "order.cancelled.v1");
        Assert.False(updates.Reader.TryRead(out _));
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<OrderSubscriptionSnapshot>("SubscribeOrder", id));
        await using var customerConnection = Connection(factory, customer.DefaultRequestHeaders.Authorization!.Parameter);
        await customerConnection.StartAsync();
        await customerConnection.InvokeAsync<OrderSubscriptionSnapshot>("SubscribeOrder", id);
        await using (var context = fixture.CreateContext())
        {
            var order = await context.Orders.SingleAsync(x => x.Id == id);
            var roles = await context.UserRoles.Where(x => x.UserId == order.CustomerId).ToArrayAsync();
            context.UserRoles.RemoveRange(roles);
            await context.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<HubException>(() => customerConnection.InvokeAsync<OrderSubscriptionSnapshot>("SubscribeOrder", id));
    }

    [Fact]
    public async Task AssignedCourierLocation_IsPrivateFreshAndPersisted_AndEtaFallsBackWhenStale()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var factory = new ApiFactory(fixture, clock);
        using var courier = await CourierAsync(factory, 24.7m);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        using var stranger = await ClientAsync(factory, "Customer");
        var response = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var pending = await customer.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}/eta");
        Assert.Equal("Low", pending.GetProperty("confidence").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/orders/{id}/eta")).StatusCode);
        await owner.PostAsync($"/api/v1/orders/{id}/accept", null);
        await DispatchAsync(factory, id);
        await using var connection = Connection(factory, customer.DefaultRequestHeaders.Authorization!.Parameter);
        await connection.StartAsync();
        var snapshot = await connection.InvokeAsync<OrderSubscriptionSnapshot>("SubscribeOrder", id);
        Assert.NotNull(snapshot.CourierLocation);
        var locations = Channel.CreateUnbounded<CourierLocationUpdate>();
        connection.On<CourierLocationUpdate>("CourierLocationUpdated", value => locations.Writer.TryWrite(value));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(HttpStatusCode.NoContent, (await courier.PutAsJsonAsync("/api/v1/couriers/me/location",
            new { latitude = 24.705m, longitude = 46.7m, observedAt = clock.GetUtcNow() })).StatusCode);
        Guid courierId;
        await using (var context = fixture.CreateContext())
        {
            courierId = (await context.Orders.SingleAsync(x => x.Id == id)).CourierId!.Value;
            var fact = await context.OutboxMessages.Where(x => x.AggregateId == courierId && x.Type == "courier.location-updated.v1")
                .OrderByDescending(x => x.Sequence).FirstAsync();
            Assert.DoesNotContain("latitude", fact.PayloadJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("longitude", fact.PayloadJson, StringComparison.OrdinalIgnoreCase);
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<RealtimeEventHandler>().HandleAsync(ToEvent(fact), CancellationToken.None);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var update = await locations.Reader.ReadAsync(timeout.Token);
        Assert.Equal(id, update.OrderId);
        Assert.Equal(courierId, update.CourierId);
        Assert.Equal(24.705m, update.Latitude);
        var fresh = await customer.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}/eta");
        Assert.Equal("Approximate", fresh.GetProperty("confidence").GetString());
        Assert.False(fresh.TryGetProperty("latitude", out _));
        clock.Advance(TimeSpan.FromMinutes(3));
        var stale = await customer.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}/eta");
        Assert.Equal("Low", stale.GetProperty("confidence").GetString());
    }

    internal static HubConnection Connection(ApiFactory factory, string? token) => new HubConnectionBuilder()
        .WithUrl("https://localhost/hubs/orders", options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            options.AccessTokenProvider = () => Task.FromResult(token);
        }).Build();

    internal static async Task PublishCommittedAsync(ApiFactory factory, Guid orderId, string type)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OrderFleet.Infrastructure.Persistence.OrderFleetDbContext>();
        var fact = await context.OutboxMessages.SingleAsync(x => x.AggregateId == orderId && x.Type == type);
        await scope.ServiceProvider.GetRequiredService<RealtimeEventHandler>().HandleAsync(ToEvent(fact), CancellationToken.None);
    }

    internal static OrderFleet.Application.Messaging.IntegrationEvent ToEvent(OrderFleet.Infrastructure.Messaging.OutboxMessage fact) =>
        new(fact.Id, fact.Type, fact.Version, fact.OccurredAtUtc, fact.AggregateId, fact.CorrelationId, fact.PayloadJson, fact.AggregateSequence);
}
