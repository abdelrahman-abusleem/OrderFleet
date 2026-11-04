
namespace OrderFleet.Domain.Orders;

public sealed class OrderItem
{
    public const int MaxNameLength = 200;

    public Guid ProductId { get; private set; }

    public string Name { get; private set; } = null!;

    public decimal UnitPrice { get; private set; }

    public int Quantity { get; private set; }

    public decimal TotalPrice { get; private set; }

    private OrderItem() { }

    public OrderItem(
        Guid productId,
        string name,
        decimal unitPrice,
        int quantity)
    {
        if (productId == Guid.Empty)
            throw new ArgumentException(
                "Product ID cannot be empty.",
                nameof(productId));

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                "Product name is required.",
                nameof(name));

        name = name.Trim();

        if (name.Length > MaxNameLength)
            throw new ArgumentException(
                $"Product name cannot exceed {MaxNameLength} characters.",
                nameof(name));

        if (unitPrice <= 0m)
            throw new ArgumentOutOfRangeException(
                nameof(unitPrice),
                "Unit price must be greater than zero.");

        if (decimal.Round(unitPrice, 2) != unitPrice)
            throw new ArgumentOutOfRangeException(
                nameof(unitPrice),
                "Unit price cannot have more than two decimal places.");

        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(quantity),
                "Quantity must be greater than zero.");

        if (!DecimalProductBounds.CanMultiply(
                unitPrice,
                quantity))
        {
            throw new ArgumentOutOfRangeException(
                nameof(unitPrice),
                "Line total exceeds the decimal range.");
        }

        ProductId = productId;
        Name = name;
        UnitPrice = unitPrice;
        Quantity = quantity;

        TotalPrice = unitPrice * quantity;
    }
}
