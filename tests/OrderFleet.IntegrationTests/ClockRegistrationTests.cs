using Microsoft.Extensions.DependencyInjection;
using OrderFleet.Application.Clock;
using OrderFleet.Infrastructure.Clock;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class ClockRegistrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ApplicationClock_WrapsTheRegisteredTimeProvider_AndRemainsSharedAcrossScopes()
    {
        var time = new TestClock(DateTimeOffset.UtcNow);
        await using var factory = new ApiFactory(fixture, time);
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        Assert.Same(time, first.ServiceProvider.GetRequiredService<TimeProvider>());
        var clock = first.ServiceProvider.GetRequiredService<IClock>();
        Assert.IsType<SystemClock>(clock);
        Assert.Same(clock, second.ServiceProvider.GetRequiredService<IClock>());
        Assert.Equal(time.GetUtcNow(), clock.UtcNow);
        time.Advance(TimeSpan.FromSeconds(37));
        Assert.Equal(time.GetUtcNow(), clock.UtcNow);
    }
}
