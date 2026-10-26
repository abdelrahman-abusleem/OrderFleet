using OrderFleet.Domain.Restaurants;

namespace OrderFleet.Domain.Tests;

public sealed class ProductCreationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Create_SetsInitialAvailabilityAndValidatedSnapshot(bool available)
    {
        var restaurant = Guid.NewGuid();
        var product = Product.Create(restaurant, "  Tea  ", 3.25m, available);
        Assert.NotEqual(Guid.Empty, product.Id);
        Assert.Equal(restaurant, product.RestaurantId);
        Assert.Equal("Tea", product.Name);
        Assert.Equal(3.25m, product.Price);
        Assert.Equal("SAR", product.Currency);
        Assert.Equal(available, product.IsAvailable);
    }
}
