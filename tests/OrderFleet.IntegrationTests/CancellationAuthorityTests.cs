using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderFleet.Domain.Orders;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class CancellationAuthorityTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("AdminCustomer", CancellationReason.AdminCancelled)]
    [InlineData("CustomerOwnerOwn", CancellationReason.CustomerRequested)]
    [InlineData("CustomerOwnerForeign", CancellationReason.RestaurantCancelled)]
    [InlineData("MemberStaff", CancellationReason.RestaurantCancelled)]
    [InlineData("ForeignStaff", null)]
    public async Task Cancel_PreservesRolePriorityAndLiveMembership(string scenario, CancellationReason? expected)
    {
        await using var factory = new ApiFactory(fixture);
        var roles = scenario == "AdminCustomer" ? new[] { "Admin", "Customer" }
            : scenario.StartsWith("CustomerOwner", StringComparison.Ordinal) ? ["Customer", "RestaurantOwner"] : new[] { "RestaurantStaff" };
        using var actor = await MultiRoleClientAsync(factory, roles);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        using var customer = await ClientAsync(factory, "Customer");
        var restaurantOwner = scenario.StartsWith("CustomerOwner", StringComparison.Ordinal) ? actor : owner;
        var (restaurant, product) = await CatalogAsync(restaurantOwner);
        if (scenario == "MemberStaff")
        {
            var id = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(actor.DefaultRequestHeaders.Authorization!.Parameter!).Subject);
            await using var context = fixture.CreateContext();
            context.RestaurantMemberships.Add(new(id, restaurant)); await context.SaveChangesAsync();
        }
        var purchaser = scenario is "AdminCustomer" or "CustomerOwnerOwn" ? actor : customer;
        var created = await CreateAsync(purchaser, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var orderId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var accepted = expected == CancellationReason.RestaurantCancelled;
        if (accepted) Assert.Equal(HttpStatusCode.NoContent, (await restaurantOwner.PostAsync($"/api/v1/orders/{orderId}/accept", null)).StatusCode);
        using var response = await actor.PostAsync($"/api/v1/orders/{orderId}/cancel", null);
        Assert.Equal(expected.HasValue ? HttpStatusCode.NoContent : HttpStatusCode.NotFound, response.StatusCode);
        await using var verify = fixture.CreateContext();
        var aggregate = await verify.Orders.SingleAsync(x => x.Id == orderId);
        Assert.Equal(expected.HasValue ? OrderStatus.Cancelled : OrderStatus.Created, aggregate.Status);
        Assert.Equal(expected, aggregate.CancelReason);
        Assert.Equal(expected.HasValue ? accepted ? 3 : 2 : 1, await verify.OrderAuditEntries.CountAsync(x => x.OrderId == orderId));
    }

    private static async Task<HttpClient> MultiRoleClientAsync(ApiFactory factory, string[] roles)
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        var password = "Aa1!" + Guid.NewGuid().ToString("N");
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
            var user = new IdentityUser<Guid> { Id = Guid.NewGuid(), Email = email, UserName = email };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            Assert.True((await users.AddToRolesAsync(user, roles)).Succeeded);
        }
        var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        return client;
    }
}
