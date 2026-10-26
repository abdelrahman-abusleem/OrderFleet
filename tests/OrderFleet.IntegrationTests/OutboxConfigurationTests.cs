using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrderFleet.Api.Configuration;
using OrderFleet.Application.Clock;
using OrderFleet.Application.Messaging;
using OrderFleet.Infrastructure.Clock;
using OrderFleet.Infrastructure.Messaging;

namespace OrderFleet.IntegrationTests;

public sealed class OutboxConfigurationTests
{
    [Fact]
    public async Task ConfiguredBatchSize_ReachesTheClaimBoundary()
    {
        var claims = new ObservedClaims();
        await using var services = Configure(2, 60, claims);
        Assert.Equal(0, await services.GetRequiredService<OutboxRelay>().RunBatchAsync(CancellationToken.None));
        Assert.Equal(2, claims.Count);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(101, 300)]
    [InlineData(11, 60)]
    public async Task UnsafeBatchOrLeaseConfiguration_IsRejected(int batchSize, int leaseSeconds)
    {
        await using var services = Configure(batchSize, leaseSeconds, new ObservedClaims());
        await Assert.ThrowsAsync<OptionsValidationException>(() => services.GetRequiredService<OutboxRelay>().RunBatchAsync(CancellationToken.None));
    }

    private static ServiceProvider Configure(int batchSize, int leaseSeconds, ObservedClaims claims)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Messaging:Enabled"] = "false",
            ["Messaging:LeaseSeconds"] = leaseSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Outbox:BatchSize"] = batchSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(new SystemClock(TimeProvider.System));
        services.AddMessaging(configuration);
        services.AddSingleton<IOutboxStore>(claims);
        return services.BuildServiceProvider();
    }

    private sealed class ObservedClaims : IOutboxStore
    {
        internal int Count { get; private set; }
        public Task<IReadOnlyList<OutboxLease>> ClaimAsync(int count, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct)
        { Count = count; return Task.FromResult<IReadOnlyList<OutboxLease>>([]); }
        public Task<bool> CompleteAsync(OutboxLease lease, DateTimeOffset now, CancellationToken ct) => throw new InvalidOperationException("No leases returned");
        public Task<bool> FailAsync(OutboxLease lease, DateTimeOffset now, string errorCode, int maxAttempts, CancellationToken ct) => throw new InvalidOperationException("No leases returned");
    }
}
