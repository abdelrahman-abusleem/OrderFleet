using OrderFleet.Domain.Orders;

namespace OrderFleet.Domain.Tests;

public class OrderItemTests
{
    [Fact]
    public void Constructor_TrimsName_PreservesProductSnapshot_AndCalculatesLineTotal()
    {
        var product = Guid.NewGuid();
        var item = new OrderItem(product, "  Chicken wrap  ", 12.75m, 3);
        Assert.Equal(product, item.ProductId);
        Assert.Equal("Chicken wrap", item.Name);
        Assert.Equal(12.75m, item.UnitPrice);
        Assert.Equal(3, item.Quantity);
        Assert.Equal(38.25m, item.TotalPrice);
    }

    [Fact]
    public void Constructor_TrimsOuterWhitespace_ButPreservesInnerSpacing()
    {
        var item = new OrderItem(Guid.NewGuid(), "  Double  space\twrap\n", 1m, 1);
        Assert.Equal("Double  space\twrap", item.Name);
    }

    [Fact]
    public void Constructor_RejectsEmptyProductId()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new OrderItem(Guid.Empty, "Wrap", 1m, 1));
        Assert.Equal("productId", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    [InlineData("\u00a0")]
    public void Constructor_RejectsMissingOrWhitespaceName(string? name)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new OrderItem(Guid.NewGuid(), name!, 1m, 1));
        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void Constructor_ExposesNameLimit_AndAcceptsANameAtTheLimit()
    {
        var name = new string('a', OrderItem.MaxNameLength);
        var item = new OrderItem(Guid.NewGuid(), name, 1m, 1);
        Assert.Equal(name, item.Name);
        Assert.Equal(OrderItem.MaxNameLength, item.Name.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_EnforcesTheNameLimitOnTrimmedContent(bool padded)
    {
        var content = new string('a', OrderItem.MaxNameLength + 1);
        var name = padded ? $"  {content}  " : content;

        var exception = Assert.Throws<ArgumentException>(() =>
            new OrderItem(Guid.NewGuid(), name, 1m, 1));
        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void Constructor_TrimIsAppliedBeforeTheLengthLimit()
    {

        var content = new string('a', OrderItem.MaxNameLength);
        var item = new OrderItem(Guid.NewGuid(), $"  {content}  ", 1m, 1);
        Assert.Equal(content, item.Name);
    }



    public static TheoryData<decimal> InvalidPrices => new()
    {
        0m, -0.01m, -1m, decimal.MinValue, 0.001m, 0.004m, 0.005m,
        0.009m, 1.001m, 1.005m, 1.999m, 0.0000000000000000000000000001m
    };

    [Theory]
    [MemberData(nameof(InvalidPrices))]
    public void Constructor_RejectsNonPositivePricesAndFractionalHalalas(decimal price)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OrderItem(Guid.NewGuid(), "Wrap", price, 1));
        Assert.Equal("unitPrice", exception.ParamName);
    }

    public static TheoryData<decimal, int, decimal> ValidPrices => new()
    {
        { 0.01m, 1, 0.01m },
        { 0.10m, 3, 0.30m },
        { 1.23m, 2, 2.46m },
        { 1.2300m, 2, 2.4600m },
        { 1.000m, 1, 1m },
        { 0.01m, int.MaxValue, 21474836.47m },
        { 7922816251426433759354395033m, 10, 79228162514264337593543950330m },
        { 39614081257132168796771975167m, 2, 79228162514264337593543950334m },
        { decimal.MaxValue, 1, decimal.MaxValue }
    };

    [Theory]
    [MemberData(nameof(ValidPrices))]
    public void Constructor_AllowsMonetaryAndQuantityBoundaries(decimal price, int quantity, decimal total)
    {
        var item = new OrderItem(Guid.NewGuid(), "Wrap", price, quantity);
        Assert.Equal(price, item.UnitPrice);
        Assert.Equal(quantity, item.Quantity);
        Assert.Equal(total, item.TotalPrice);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Constructor_RejectsNonPositiveQuantity(int quantity)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OrderItem(Guid.NewGuid(), "Wrap", 1m, quantity));
        Assert.Equal("quantity", exception.ParamName);
    }





    public static TheoryData<decimal, int> OverflowingLineTotals => new()
    {
        { decimal.MaxValue, 2 },
        { decimal.MaxValue - 1, 3 },
        { 39614081257132168796771975168m, 2 },
        { 7922816251426433759354395034m, 10 }
    };

    [Theory]
    [MemberData(nameof(OverflowingLineTotals))]
    public void Constructor_RejectsLineTotalBeyondDecimalRange(decimal price, int quantity)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OrderItem(Guid.NewGuid(), "Wrap", price, quantity));
        Assert.Equal("unitPrice", exception.ParamName);
    }

    [Fact]
    public void Constructor_AcceptsTheHighestSafelyRepresentableLineTotalForQuantity()
    {


        var item = new OrderItem(Guid.NewGuid(), "Wrap", 19807040628566084398385987583m, 4);
        Assert.Equal(19807040628566084398385987583m, item.UnitPrice);
        Assert.Equal(decimal.MaxValue - 3m, item.TotalPrice);
    }








    [Fact]
    public void Constructor_RejectsItsOwnGuardBoundary_ForEveryQuantity_Cleanly()
    {
        var overflowQuantities = new List<int>();

        for (var quantity = 2; quantity <= 5_000; quantity++)
        {
            var admittedBoundary = decimal.Truncate(decimal.MaxValue / quantity);
            try
            {
                _ = new OrderItem(Guid.NewGuid(), "Wrap", admittedBoundary, quantity);
            }
            catch (ArgumentOutOfRangeException)
            {

            }
            catch (OverflowException)
            {


                overflowQuantities.Add(quantity);
            }
        }

        Assert.True(
            overflowQuantities.Count == 0,
            "The line-total guard admits unit prices whose line total is " +
            "unrepresentable; constructing them leaks OverflowException at " +
            $"quantities: {string.Join(", ", overflowQuantities)}");
    }




    [Fact]
    public void Constructor_RejectsTheGuardBoundaryWhoseLineTotalExceedsDecimalMaximum()
    {
        var boundary = decimal.Truncate(decimal.MaxValue / 4m);

        Assert.Equal(19807040628566084398385987584m, boundary);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OrderItem(Guid.NewGuid(), "Wrap", boundary, 4));
        Assert.Equal("unitPrice", exception.ParamName);
    }
}
