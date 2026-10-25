using OrderFleet.Application.Eta;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "Unit")]
public sealed class EtaCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly EtaCalculator calculator = new(new(30, 1.25, 10, 2, 120), TimeSpan.FromMinutes(2));

    [Fact]
    public void FreshPickup_UsesCourierDistance_AndLabelsApproximation()
    {
        var result = calculator.Calculate(new("PickedUp", 20000, 0, 1000, Now, Now.AddMinutes(-10)), Now);
        Assert.Equal(5, result.Minutes); // 1km *1.25 /30kmh =2.5m, plus2m handoff, rounded up.
        Assert.Equal("Approximate", result.Confidence);
        Assert.True(result.IsApproximate);
    }

    [Fact]
    public void StaleOrMissingLocation_UsesExplicitConservativeFallback()
    {
        var stale = calculator.Calculate(new("PickedUp", 20000, 0, 1, Now.AddMinutes(-3), Now), Now);
        var missing = calculator.Calculate(new("PickedUp", 20000, null, null, null, Now), Now);
        Assert.Equal("Low", stale.Confidence);
        Assert.Equal(missing.Minutes, stale.Minutes);
        Assert.Contains("fallback", stale.Basis, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Delivered", 0)]
    [InlineData("Cancelled", null)]
    public void TerminalStates_DoNotPromiseFutureDelivery(string status, int? expected) =>
        Assert.Equal(expected, calculator.Calculate(new(status, 1000, null, null, null, Now), Now).Minutes);

    [Fact]
    public void HugeDistance_IsBounded() => Assert.Equal(120,
        calculator.Calculate(new("Created", 20000000, null, null, null, Now), Now).Minutes);
}
