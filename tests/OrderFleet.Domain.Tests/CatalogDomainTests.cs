using OrderFleet.Domain.Geo;
using OrderFleet.Domain.Restaurants;

namespace OrderFleet.Domain.Tests;

public sealed class CatalogDomainTests
{
    [Theory]
    [InlineData(-90, -180)]
    [InlineData(90, 180)]
    [InlineData(0, 0)]
    public void Coordinates_AcceptsRangeBoundaries(int latitude, int longitude)
    {
        var coordinates = new Coordinates(latitude, longitude);
        Assert.Equal(latitude, coordinates.Latitude);
        Assert.Equal(longitude, coordinates.Longitude);
    }

    [Theory]
    [InlineData(-91, 0)]
    [InlineData(91, 0)]
    [InlineData(0, -181)]
    [InlineData(0, 181)]
    public void Coordinates_RejectsOutOfRangeValues(int latitude, int longitude) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Coordinates(latitude, longitude));

    [Fact]
    public void CatalogChanges_DoNotChangeExistingOrderItemSnapshot()
    {
        var restaurant = Restaurant.Create("  Demo kitchen  ", new(24.7m, 46.7m));
        var product = Product.Create(restaurant.Id, "  Tea  ", 3.25m);
        var snapshot = new OrderFleet.Domain.Orders.OrderItem(product.Id, product.Name, product.Price, 2);
        product.Update("Large tea", 4.50m, false);
        restaurant.SetActive(false);
        Assert.Equal("Demo kitchen", restaurant.Name);
        Assert.False(restaurant.IsActive);
        Assert.False(product.IsAvailable);
        Assert.Equal("Tea", snapshot.Name);
        Assert.Equal(3.25m, snapshot.UnitPrice);
        Assert.Equal(6.50m, snapshot.TotalPrice);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    public void Restaurant_RejectsMissingName(string name) =>
        Assert.Throws<ArgumentException>(() => Restaurant.Create(name, new(0, 0)));

    [Fact]
    public void InvalidProductUpdate_LeavesProductUnchanged()
    {
        var product = Product.Create(Guid.NewGuid(), "Tea", 3.25m);
        Assert.Throws<ArgumentOutOfRangeException>(() => product.Update("Changed", 0.001m, false));
        Assert.Equal("Tea", product.Name);
        Assert.Equal(3.25m, product.Price);
        Assert.True(product.IsAvailable);
    }
}
