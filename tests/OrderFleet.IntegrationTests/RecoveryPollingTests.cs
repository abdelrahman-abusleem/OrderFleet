using OrderFleet.Infrastructure.Recovery;

namespace OrderFleet.IntegrationTests;

public sealed class RecoveryPollingTests
{
    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1, 2000)]
    [InlineData(2, 4000)]
    [InlineData(3, 8000)]
    [InlineData(4, 10000)]
    [InlineData(1000, 10000)]
    public void IdlePolling_IsBounded_AndResetsToBaseWhenWorkIsFound(int emptySweeps, int expected)
    {
        var options = new RecoveryOptions();
        Assert.Equal(expected, options.PollDelayMilliseconds(emptySweeps));
        Assert.Equal(1000, options.PollDelayMilliseconds(0));
    }
}
