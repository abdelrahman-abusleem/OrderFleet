
namespace OrderFleet.Domain.Orders;

public sealed class OrderPricingLimits
{
    public int MaxQuantityPerItem { get; }

    public int MaxItemsPerOrder { get; }

    public decimal MaxUnitPrice { get; }

    public decimal MaxOrderTotal { get; }

    public OrderPricingLimits(
        int maxQuantityPerItem,
        int maxItemsPerOrder,
        decimal maxUnitPrice,
        decimal maxOrderTotal)
    {
        if (maxQuantityPerItem <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxQuantityPerItem));

        if (maxItemsPerOrder <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxItemsPerOrder));

        if (maxUnitPrice <= 0m ||
            decimal.Round(maxUnitPrice, 2) != maxUnitPrice)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxUnitPrice),
                "Maximum unit price must be positive " +
                "and have at most two decimal places.");
        }

        if (maxOrderTotal <= 0m ||
            decimal.Round(maxOrderTotal, 2) != maxOrderTotal)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOrderTotal),
                "Maximum order total must be positive " +
                "and have at most two decimal places.");
        }

        if (maxOrderTotal < maxUnitPrice)
        {
            throw new ArgumentException(
                "Maximum order total cannot be less " +
                "than maximum unit price.",
                nameof(maxOrderTotal));
        }

        decimal maxUnits =
            (decimal)maxQuantityPerItem * maxItemsPerOrder;

        if (!DecimalProductBounds.CanMultiply(
                maxUnitPrice,
                maxUnits))
        {
            throw new ArgumentException(
                "Configured monetary limits are too large.",
                nameof(maxUnitPrice));
        }

        MaxQuantityPerItem = maxQuantityPerItem;
        MaxItemsPerOrder = maxItemsPerOrder;
        MaxUnitPrice = maxUnitPrice;
        MaxOrderTotal = maxOrderTotal;
    }
}


internal static class DecimalProductBounds
{
    internal static bool CanMultiply(
        decimal positiveValue,
        decimal positiveIntegerMultiplier)
    {


        decimal remainder =
            decimal.MaxValue % positiveIntegerMultiplier;


        decimal maximumWholeValue =
            (decimal.MaxValue - remainder)
            / positiveIntegerMultiplier;

        if (positiveValue <= maximumWholeValue)
            return true;

        decimal excess = positiveValue - maximumWholeValue;

        if (excess >= 1m)
            return false;

        return excess * positiveIntegerMultiplier <= remainder;
    }
}
