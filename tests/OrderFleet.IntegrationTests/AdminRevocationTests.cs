using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderFleet.Domain.Orders;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class AdminRevocationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("read")]
    [InlineData("delivery")]
    [InlineData("eta")]
    [InlineData("cancel")]
    [InlineData("release-assignment")]
    [InlineData("nearby")]
    [InlineData("operations")]
    public async Task RevokedAdminToken_LosesEveryAdministrativeHttpPath(string action)
    {
        await using var factory = new ApiFactory(fixture);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        using var customer = await ClientAsync(factory, "Customer");
        using var admin = await ClientAsync(factory, "Admin");
        var (restaurant, product) = await CatalogAsync(owner);
        var created = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/orders/{id}")).StatusCode);
        var actor = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetGuid();
        await using (var context = fixture.CreateContext())
        {
            var adminRole = await context.Roles.Where(x => x.Name == "Admin").Select(x => x.Id).SingleAsync();
            await context.UserRoles.Where(x => x.UserId == actor && x.RoleId == adminRole).ExecuteDeleteAsync();
        }
        var response = action switch
        {
            "read" => await admin.GetAsync($"/api/v1/orders/{id}"),
            "delivery" => await admin.GetAsync($"/api/v1/orders/{id}/delivery"),
            "eta" => await admin.GetAsync($"/api/v1/orders/{id}/eta"),
            "cancel" => await admin.PostAsync($"/api/v1/orders/{id}/cancel", null),
            "release-assignment" => await admin.PostAsJsonAsync($"/api/v1/orders/{id}/release-assignment", new { assignmentId = Guid.NewGuid() }),
            "nearby" => await admin.GetAsync($"/api/v1/restaurants/{restaurant}/nearby-couriers"),
            _ => await admin.GetAsync("/api/v1/operations/summary")
        };
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(OrderStatus.Created, (await verify.Orders.SingleAsync(x => x.Id == id)).Status);
        Assert.DoesNotContain("latitude", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}
