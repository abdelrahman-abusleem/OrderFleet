using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class AuthenticatedRateLimitTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAndWriteLimits_RejectAtBoundary_AndSeparateActors(bool write)
    {
        await using var factory = new ApiFactory(fixture);
        factory.Overrides[write ? "RequestLimits:WritePerMinute" : "RequestLimits:ReadPerMinute"] = "2";
        using var first = await ClientAsync(factory, "Customer");
        using var other = await ClientAsync(factory, "Customer");
        async Task<HttpResponseMessage> Send(HttpClient client) => write
            ? await client.PostAsync($"/api/v1/orders/{Guid.NewGuid()}/cancel", null)
            : await client.GetAsync($"/api/v1/orders/{Guid.NewGuid()}");
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.NotFound, (await Send(first)).StatusCode);
        var rejected = await Send(first);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(other)).StatusCode);
    }

    [Fact]
    public async Task CreateLimit_RejectsBeforePersistingAnotherOrder()
    {
        await using var factory = new ApiFactory(fixture);
        factory.Overrides["RequestLimits:CreatePerMinute"] = "2";
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        using var customer = await ClientAsync(factory, "Customer");
        var actor = (await customer.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetGuid();
        var (restaurant, product) = await CatalogAsync(owner);
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Created, (await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"))).StatusCode);
        using var variedCase = new HttpRequestMessage(HttpMethod.Post, "/API/v1/Orders/")
        { Content = JsonContent.Create(Request(restaurant, product)) };
        variedCase.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await customer.SendAsync(variedCase)).StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(2, await verify.Orders.CountAsync(x => x.CustomerId == actor));
    }

    [Fact]
    public async Task HubLimit_IsSharedAcrossConnections_AndSeparatesActors()
    {
        await using var factory = new ApiFactory(fixture);
        factory.Overrides["RequestLimits:HubPerMinute"] = "2";
        using var actor = await ClientAsync(factory, "Customer");
        using var other = await ClientAsync(factory, "Customer");
        await using var first = RealtimeApiTests.Connection(factory, actor.DefaultRequestHeaders.Authorization!.Parameter);
        await using var second = RealtimeApiTests.Connection(factory, actor.DefaultRequestHeaders.Authorization!.Parameter);
        await using var separate = RealtimeApiTests.Connection(factory, other.DefaultRequestHeaders.Authorization!.Parameter);
        await first.StartAsync();
        await second.StartAsync();
        await separate.StartAsync();
        await first.InvokeAsync("UnsubscribeOrder", Guid.NewGuid());
        await second.InvokeAsync("UnsubscribeOrder", Guid.NewGuid());
        var rejected = await Assert.ThrowsAsync<HubException>(() => first.InvokeAsync("UnsubscribeOrder", Guid.NewGuid()));
        Assert.Contains("Too many requests", rejected.Message);
        await separate.InvokeAsync("UnsubscribeOrder", Guid.NewGuid());
    }
}
