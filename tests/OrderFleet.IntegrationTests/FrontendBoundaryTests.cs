using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.Net.WebSockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;
using static OrderFleet.IntegrationTests.DispatchConcurrencyTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class FrontendBoundaryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task BrowserWebSocket_EnforcesOriginBeforeHandshake()
    {
        await using var factory = new ApiFactory(fixture);
        factory.Overrides["Cors:AllowedOrigins:0"] = "https://frontend.example.test";
        using var customer = await ClientAsync(factory, "Customer");
        var uri = new Uri("wss://localhost/hubs/orders?access_token=" + Uri.EscapeDataString(customer.DefaultRequestHeaders.Authorization!.Parameter!));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var denied = factory.Server.CreateWebSocketClient();
        denied.ConfigureRequest = request => request.Headers.Origin = "https://foreign.example.test";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => denied.ConnectAsync(uri, timeout.Token));
        Assert.Contains("403", error.Message);
        var allowed = factory.Server.CreateWebSocketClient();
        allowed.ConfigureRequest = request => request.Headers.Origin = "https://frontend.example.test";
        using var socket = await allowed.ConnectAsync(uri, timeout.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[1024];
        var response = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Contains("{}", Encoding.UTF8.GetString(buffer, 0, response.Count));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);
    }

    [Fact]
    public async Task Cors_OnlyAllowsConfiguredOrigin_WithCredentials_AndRejectsForeignHubOrigin()
    {
        await using var factory = new ApiFactory(fixture);
        factory.Overrides["Cors:AllowedOrigins:0"] = "https://frontend.example.test";
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        foreach (var allowed in new[] { true, false })
        {
            var origin = allowed ? "https://frontend.example.test" : "https://foreign.example.test";
            using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/orders");
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,idempotency-key");
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            if (allowed)
            {
                Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
                Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
            }
            else Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }
        using var customer = await ClientAsync(factory, "Customer");
        foreach (var allowed in new[] { true, false })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/hubs/orders/negotiate?negotiateVersion=1");
            request.Headers.Add("Origin", allowed ? "https://frontend.example.test" : "https://foreign.example.test");
            var response = await customer.SendAsync(request);
            Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://user:secret@example.test")]
    [InlineData("https://frontend.example.test/path")]
    public async Task InvalidCorsOrigin_FailsStartup(string origin)
    {
        await using var factory = new ApiFactory(fixture);
        factory.Overrides["Cors:AllowedOrigins:0"] = origin;
        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task CourierListing_IsPrivatePagedAndExcludesReleasedAssignments()
    {
        await using var factory = new ApiFactory(fixture);
        using var courier = await CourierAsync(factory, 18);
        using var stranger = await ClientAsync(factory, "Courier");
        using var customer = await ClientAsync(factory, "Customer");
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, 18);
        var id = await AcceptedAsync(factory, owner, restaurant, product);
        Assert.Equal(OrderFleet.Application.Dispatch.DispatchOutcome.Assigned, await DispatchAsync(factory, id));
        var response = await courier.GetAsync("/api/v1/couriers/me/orders?page=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var order = Assert.Single(body.EnumerateArray());
        Assert.Equal(id, order.GetProperty("id").GetGuid());
        Assert.DoesNotContain("latitude", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty((await stranger.GetFromJsonAsync<JsonElement>("/api/v1/couriers/me/orders")).EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/couriers/me/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await courier.GetAsync("/api/v1/couriers/me/orders?page=0")).StatusCode);
        Assert.Empty((await courier.GetFromJsonAsync<JsonElement>("/api/v1/couriers/me/orders?page=2&pageSize=1")).EnumerateArray());
        var assignmentId = order.GetProperty("assignmentId").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await courier.PostAsJsonAsync($"/api/v1/orders/{id}/release-assignment", new { assignmentId })).StatusCode);
        Assert.Empty((await courier.GetFromJsonAsync<JsonElement>("/api/v1/couriers/me/orders")).EnumerateArray());
        await using var verify = fixture.CreateContext();
        Assert.Null((await verify.Orders.SingleAsync(x => x.Id == id)).AssignmentId);
    }
}
