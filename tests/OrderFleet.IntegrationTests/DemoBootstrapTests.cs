using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.Internal;
using OrderFleet.Api.Development;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class DemoBootstrapTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("Production", "A-valid-private-password123!")]
    [InlineData("Testing", "A-valid-private-password123!")]
    [InlineData("Development", null)]
    [InlineData("Development", "short")]
    public async Task UnsafeSeedInvocation_IsRejectedWithoutWriting(string environment, string? password)
    {
        await using var factory = new ApiFactory(fixture);
        await using var before = fixture.CreateContext();
        var count = await before.Users.CountAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DemoBootstrap.ExecuteAsync(factory.Services,
            new HostingEnvironment { EnvironmentName = environment }, password, CancellationToken.None));
        Assert.Equal(count, await before.Users.CountAsync());
    }

    [Fact]
    public async Task ExplicitSeed_IsAtomicIdempotent_AndNeverResetsPasswordsOrActiveCourierState()
    {
        await using var factory = new ApiFactory(fixture);
        var password = "Aa1!" + Guid.NewGuid().ToString("N");
        var environment = new HostingEnvironment { EnvironmentName = "Development" };
        var first = await DemoBootstrap.ExecuteAsync(factory.Services, environment, password, CancellationToken.None);
        await using var before = fixture.CreateContext();
        var hash = (await before.Users.SingleAsync(x => x.Email == "owner@demo.orderfleet.test")).PasswordHash;
        var courier = await before.Couriers.SingleAsync();
        courier.SetAvailable(true);
        await before.SaveChangesAsync();
        var second = await DemoBootstrap.ExecuteAsync(factory.Services, environment, "Different-private-password123!", CancellationToken.None);
        Assert.Equal(first, second);
        await using var verify = fixture.CreateContext();
        Assert.Equal(6, await verify.Users.CountAsync(x => x.Email!.EndsWith("@demo.orderfleet.test")));
        Assert.Equal(hash, (await verify.Users.SingleAsync(x => x.Email == "owner@demo.orderfleet.test")).PasswordHash);
        Assert.Equal(OrderFleet.Domain.Couriers.CourierStatus.Available, (await verify.Couriers.SingleAsync()).Status);
        Assert.Equal(2, await verify.RestaurantMemberships.CountAsync(x => x.RestaurantId == first.RestaurantId));
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "owner@demo.orderfleet.test", password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(25.50m, (await verify.Products.SingleAsync(x => x.Id == first.ProductId)).Price);
    }
}
