using OrderFleet.Domain.Orders;

namespace OrderFleet.Domain.Tests;

public class OrderPricingLimitsTests
{
    public static TheoryData<int> NonPositiveIntegers => new() { 0, -1, int.MinValue };





    public static TheoryData<decimal> InvalidMonetaryLimits => new()
    {
        0m, -0.01m, -1m, decimal.MinValue, 0.001m, 0.004m, 0.005m, 1.001m, 1.005m,
        1.999m, 1234567890123456789012.3456m
    };

    [Theory]
    [MemberData(nameof(NonPositiveIntegers))]
    public void Constructor_RejectsNonPositiveQuantityLimit(int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OrderPricingLimits(limit, 20, 9_999.99m, 99_999.99m));

    [Theory]
    [MemberData(nameof(NonPositiveIntegers))]
    public void Constructor_RejectsNonPositiveItemCountLimit(int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OrderPricingLimits(99, limit, 9_999.99m, 99_999.99m));

    [Theory]
    [MemberData(nameof(InvalidMonetaryLimits))]
    public void Constructor_RejectsInvalidUnitPriceLimit(decimal limit)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OrderPricingLimits(99, 20, limit, 99_999.99m));
        Assert.Equal("maxUnitPrice", exception.ParamName);
    }

    [Theory]
    [MemberData(nameof(InvalidMonetaryLimits))]
    public void Constructor_RejectsInvalidOrderTotalLimit(decimal limit)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OrderPricingLimits(99, 20, 9_999.99m, limit));
        Assert.Equal("maxOrderTotal", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsOrderTotalBelowUnitPriceLimit()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new OrderPricingLimits(99, 20, 9_999.99m, 9_999.98m));
        Assert.Equal("maxOrderTotal", exception.ParamName);
    }

    [Fact]
    public void Constructor_AcceptsOrderTotalEqualToUnitPriceLimit()
    {
        var limits = new OrderPricingLimits(99, 20, 9_999.99m, 9_999.99m);

        Assert.Equal(99, limits.MaxQuantityPerItem);
        Assert.Equal(20, limits.MaxItemsPerOrder);
        Assert.Equal(9_999.99m, limits.MaxUnitPrice);
        Assert.Equal(9_999.99m, limits.MaxOrderTotal);
    }

    [Fact]
    public void Constructor_PreservesTheConfiguredBounds()
    {
        var limits = new OrderPricingLimits(30, 50, 2_500.55m, 7_777.77m);

        Assert.Equal(30, limits.MaxQuantityPerItem);
        Assert.Equal(50, limits.MaxItemsPerOrder);
        Assert.Equal(2_500.55m, limits.MaxUnitPrice);
        Assert.Equal(7_777.77m, limits.MaxOrderTotal);
    }




    [Fact]
    public void Constructor_AcceptsTheHighestSafelyRepresentablePriceLimit()
    {

        var limits = new OrderPricingLimits(2, 2, 19807040628566084398385987583m, decimal.MaxValue);

        Assert.Equal(19807040628566084398385987583m, limits.MaxUnitPrice);
        Assert.Equal(decimal.MaxValue, limits.MaxOrderTotal);
    }

    [Fact]
    public void Constructor_RejectsPriceLimitsBeyondTheAggregateDecimalRange()
    {


        var exception = Assert.Throws<ArgumentException>(() =>
            new OrderPricingLimits(2, 1, decimal.MaxValue, decimal.MaxValue));
        Assert.Equal("maxUnitPrice", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsPriceLimitsThatOverflowThroughTheItemCount()
    {

        var exception = Assert.Throws<ArgumentException>(() =>
            new OrderPricingLimits(2, 2, 39614081257132168796771975168m, decimal.MaxValue));
        Assert.Equal("maxUnitPrice", exception.ParamName);
    }







    [Fact]
    public void Constructor_RejectsTheDivisionRoundUpBoundary_WithADomainException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new OrderPricingLimits(2, 2, 19807040628566084398385987584m, decimal.MaxValue));
        Assert.Equal("maxUnitPrice", exception.ParamName);
    }





    [Fact]
    public void Constructor_NeverAdmitsAPriceLimit_WhoseMaximalAggregationOverflowsDecimal()
    {
        var violations = new List<int>();

        for (var units = 2; units <= 5_000; units++)
        {
            var admittedBoundary = decimal.Truncate(decimal.MaxValue / units);

            var admitted = false;
            try
            {
                _ = new OrderPricingLimits(units, 1, admittedBoundary, decimal.MaxValue);
                admitted = true;
            }
            catch (ArgumentException)
            {

            }

            if (!admitted)
                continue;

            try
            {
                _ = admittedBoundary * units;
            }
            catch (OverflowException)
            {
                violations.Add(units);
            }
        }

        Assert.True(
            violations.Count == 0,
            "OrderPricingLimits admits price/unit-count combinations whose " +
            "maximal aggregation overflows decimal at maxUnits values: " +
            string.Join(", ", violations));
    }
}
