using Microsoft.Extensions.DependencyInjection;
using OrderFleet.Application.Orders;
using OrderFleet.Infrastructure.Persistence;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class OrderingCapabilityTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ScopedCapabilities_ResolveOneStore_AndDifferentScopesRemainIsolated()
    {
        await using var factory = new ApiFactory(fixture);
        await using var first = factory.Services.CreateAsyncScope();
        var creation = first.ServiceProvider.GetRequiredService<IOrderCreationStore>();
        Assert.IsType<EfOrderingStore>(creation);
        Assert.Same(creation, first.ServiceProvider.GetRequiredService<IOrderTransitionStore>());
        Assert.Same(creation, first.ServiceProvider.GetRequiredService<IOrderQueryStore>());
        await using var second = factory.Services.CreateAsyncScope();
        Assert.NotSame(creation, second.ServiceProvider.GetRequiredService<IOrderCreationStore>());
    }
}
