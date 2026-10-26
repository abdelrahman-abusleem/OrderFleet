using Microsoft.Extensions.DependencyInjection;
using OrderFleet.Application.Recovery;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;
using static OrderFleet.IntegrationTests.DispatchConcurrencyTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class RecoveryWorkCountTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Sweep_ReportsIdleThenWorkThenIdle_AfterTimeoutAndCompensation()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var factory = new ApiFactory(fixture, clock);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        using var customer = await ClientAsync(factory, "Customer");
        using var scope = factory.Services.CreateScope();
        var recovery = scope.ServiceProvider.GetRequiredService<RecoverOrderFleet>();
        Assert.Equal(0, await recovery.ExecuteAsync(CancellationToken.None));
        var (restaurant, product) = await CatalogForDispatchAsync(owner, 20);
        Assert.Equal(System.Net.HttpStatusCode.Created,
            (await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"))).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(2, await recovery.ExecuteAsync(CancellationToken.None));
        Assert.Equal(0, await recovery.ExecuteAsync(CancellationToken.None));
    }
}
