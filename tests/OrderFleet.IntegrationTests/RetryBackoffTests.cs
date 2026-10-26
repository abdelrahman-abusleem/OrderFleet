using OrderFleet.Infrastructure.Reliability;

namespace OrderFleet.IntegrationTests;

public sealed class RetryBackoffTests
{
    [Theory]
    [InlineData(1, 2.2)]
    [InlineData(2, 4.4)]
    [InlineData(100, 300)]
    public void Jitter_IsDeterministicWithInjectedRandom_AndNeverExceedsCap(int attempt, double expectedSeconds)
    {
        var retry = new JitteredRetryBackoff(new FixedRandom());
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), retry.GetDelay(attempt, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(300)));
    }

    [Fact]
    public void InvalidPolicy_IsRejected()
    {
        var retry = new JitteredRetryBackoff(new FixedRandom());
        Assert.Throws<ArgumentOutOfRangeException>(() => retry.GetDelay(0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)));
        Assert.Throws<ArgumentOutOfRangeException>(() => retry.GetDelay(1, TimeSpan.Zero, TimeSpan.FromSeconds(30)));
        Assert.Throws<ArgumentOutOfRangeException>(() => retry.GetDelay(1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));
    }

    private sealed class FixedRandom : Random
    {
        public override double NextDouble() => 0.5;
    }
}
