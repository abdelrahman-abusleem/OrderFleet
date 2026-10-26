using OrderFleet.Domain.Common;

namespace OrderFleet.Domain.Restaurants;

public sealed class Product
{
    public Guid Id { get; private set; }
    public Guid RestaurantId { get; private set; }
    public string Name { get; private set; } = "";
    public decimal Price { get; private set; }
    public string Currency { get; private set; } = CurrencyCodes.Sar;
    public bool IsAvailable { get; private set; }

    private Product() { }

    public static Product Create(Guid restaurantId, string name, decimal price, bool available = true)
    {
        if (restaurantId == Guid.Empty) throw new ArgumentException("Restaurant is required.", nameof(restaurantId));
        var product = new Product { Id = Guid.NewGuid(), RestaurantId = restaurantId };
        product.Update(name, price, available);
        return product;
    }

    public void Update(string name, decimal price, bool available)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            throw new ArgumentException("Product name is required and limited to 200 characters.", nameof(name));
        if (price <= 0 || decimal.Round(price, 2) != price)
            throw new ArgumentOutOfRangeException(nameof(price));
        Name = name.Trim();
        Price = price;
        IsAvailable = available;
    }
}
