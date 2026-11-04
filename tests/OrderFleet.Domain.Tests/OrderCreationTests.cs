using System.Collections;
using OrderFleet.Domain.Orders;
using static OrderFleet.Domain.Tests.OrderTestSupport;

namespace OrderFleet.Domain.Tests;

public class OrderCreationTests
{
    [Fact]
    public void Create_PreservesCustomerRestaurantAndItems_AndInitializesAggregate()
    {
        var customer = Guid.NewGuid();
        var restaurant = Guid.NewGuid();
        var items = new[] { Item(10.25m, 3), Item(0.10m, 7), Item(0.69m, 1) };

        var order = Order.Create(customer, restaurant, items, Limits(), Now);

        Assert.NotEqual(Guid.Empty, order.Id);
        Assert.Equal(customer, order.CustomerId);
        Assert.Equal(restaurant, order.RestaurantId);
        Assert.Equal(OrderStatus.Created, order.Status);
        Assert.Equal("SAR", order.Currency);
        Assert.Null(order.CourierId);
        Assert.Null(order.AssignmentId);
        Assert.Null(order.CancelReason);
        Assert.Equal(items, order.Items);
        Assert.Equal(32.14m, order.TotalAmount);
    }

    [Fact]
    public void Create_GeneratesDistinctOrderIds()
    {
        Assert.NotEqual(Create().Id, Create().Id);
    }

    [Theory]
    [InlineData(true, "customerId")]
    [InlineData(false, "restaurantId")]
    public void Create_RejectsEmptyIdentity(bool emptyCustomer, string parameter)
    {
        var exception = Assert.Throws<ArgumentException>(() => Order.Create(
            emptyCustomer ? Guid.Empty : Guid.NewGuid(),
            emptyCustomer ? Guid.NewGuid() : Guid.Empty, [Item()], Limits(), Now));
        Assert.Equal(parameter, exception.ParamName);
    }

    [Fact]
    public void Create_RejectsNullItems()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            Order.Create(Guid.NewGuid(), Guid.NewGuid(), null!, Limits(), Now));
        Assert.Equal("items", exception.ParamName);
    }

    [Fact]
    public void Create_RejectsNullLimits()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            Order.Create(Guid.NewGuid(), Guid.NewGuid(), [Item()], null!, Now));
        Assert.Equal("limits", exception.ParamName);
    }

    [Fact]
    public void Create_RejectsEmptyItems()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Order.Create(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<OrderItem>(), Limits(), Now));
        Assert.Equal("items", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Create_RejectsNullItemAtAnyPosition(int position)
    {
        var items = new[] { Item(), Item(), Item() };
        items[position] = null!;
        var exception = Assert.Throws<ArgumentException>(() =>
            Order.Create(Guid.NewGuid(), Guid.NewGuid(), items, Limits(), Now));
        Assert.Equal("items", exception.ParamName);
    }

    [Fact]
    public void Create_RejectsDuplicateProductIds_EvenWhenOtherwiseCompliant()
    {
        var product = Guid.NewGuid();
        var items = new[]
        {
            new OrderItem(product, "Tea", 1m, 1),
            new OrderItem(product, "Tea", 2m, 2),
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            Order.Create(Guid.NewGuid(), Guid.NewGuid(), items, Limits(), Now));
        Assert.Equal("items", exception.ParamName);
        Assert.Contains("more than once", exception.Message);
    }

    [Fact]
    public void Create_AllowsDifferentProductsSharingTheSameName()
    {
        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(),
            [new OrderItem(Guid.NewGuid(), "Tea", 1m, 1),
             new OrderItem(Guid.NewGuid(), "Tea", 2m, 1)],
            Limits(), Now);

        Assert.Equal(2, order.Items.Count);
        Assert.Equal(3m, order.TotalAmount);
    }

    [Fact]
    public void Create_SnapshotsCallerList()
    {
        var first = Item();
        var second = Item(4m, 3);
        var source = new List<OrderItem> { first, second };
        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), source, Limits(), Now);
        var snapshot = new Snapshot(order);

        source[0] = Item(999m);
        source.RemoveAt(1);
        source.Add(Item());
        source.Clear();

        snapshot.AssertUnchanged(order);
    }

    [Fact]
    public void Create_SnapshotsCallerArray()
    {
        var source = new[] { Item() };
        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), source, Limits(), Now);
        var snapshot = new Snapshot(order);
        source[0] = Item(100m);
        snapshot.AssertUnchanged(order);
    }

    [Fact]
    public void Create_EnumeratesSourceOnce_AndDoesNotRetainDeferredSource()
    {
        var enumerations = 0;
        var price = 2m;
        IEnumerable<OrderItem> Source()
        {
            enumerations++;
            if (enumerations > 1) throw new InvalidOperationException("Source was enumerated twice.");
            yield return Item(price, 3);
        }

        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), Source(), Limits(), Now);
        price = 99m;
        Assert.Single(order.Items);
        Assert.Equal(6m, order.TotalAmount);
        Assert.Equal(6m, order.TotalAmount);
        Assert.Equal(1, enumerations);
    }

    [Fact]
    public void Items_RejectsMutationThroughGenericAndNonGenericListInterfaces()
    {
        var order = Create();
        var snapshot = new Snapshot(order);
        var generic = Assert.IsAssignableFrom<IList<OrderItem>>(order.Items);
        var nonGeneric = Assert.IsAssignableFrom<IList>(order.Items);
        Assert.True(generic.IsReadOnly);
        Assert.True(nonGeneric.IsReadOnly);

        Action[] mutations =
        [
            () => generic.Add(Item()), () => generic.Insert(0, Item()),
            () => generic[0] = Item(), () => generic.Remove(order.Items[0]),
            () => generic.RemoveAt(0), () => generic.Clear(),
            () => nonGeneric.Add(Item()), () => nonGeneric.Insert(0, Item()),
            () => nonGeneric[0] = Item(), () => nonGeneric.Remove(order.Items[0]),
            () => nonGeneric.RemoveAt(0), () => nonGeneric.Clear()
        ];
        foreach (var mutate in mutations)
        {
            Assert.Throws<NotSupportedException>(mutate);
            snapshot.AssertUnchanged(order);
        }
    }





    [Fact]
    public void Create_RejectsIntrinsicallyValidItems_AboveTheQuantityPolicyLimit()
    {
        var limits = new OrderPricingLimits(30, 20, 9_999.99m, 99_999.99m);
        var item = new OrderItem(Guid.NewGuid(), "Family platter", 1m, 31);

        var exception = Assert.Throws<ArgumentException>(() =>
            Order.Create(Guid.NewGuid(), Guid.NewGuid(), [item], limits, Now));
        Assert.Equal("items", exception.ParamName);
        Assert.Contains("30", exception.Message);
    }

    [Fact]
    public void Create_AllowsQuantityExactlyAtThePolicyLimit()
    {
        var limits = new OrderPricingLimits(30, 20, 9_999.99m, 99_999.99m);

        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), [Item(1m, 30)], limits, Now);
        Assert.Equal(30m, order.TotalAmount);
        Assert.Equal(30, order.Items.Single().Quantity);
    }

    [Fact]
    public void Create_RejectsIntrinsicallyValidItems_AboveTheUnitPricePolicyLimit()
    {
        var limits = new OrderPricingLimits(30, 20, 9_999.99m, 99_999.99m);
        var item = new OrderItem(Guid.NewGuid(), "Catering tray", 10_000m, 1);

        var exception = Assert.Throws<ArgumentException>(() =>
            Order.Create(Guid.NewGuid(), Guid.NewGuid(), [item], limits, Now));
        Assert.Equal("items", exception.ParamName);
        Assert.Contains("9999.99", exception.Message);
    }

    [Fact]
    public void Create_AllowsUnitPriceExactlyAtThePolicyLimit()
    {
        var limits = new OrderPricingLimits(30, 20, 9_999.99m, 99_999.99m);

        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), [Item(9_999.99m, 1)], limits, Now);
        Assert.Equal(9_999.99m, order.TotalAmount);
    }

    [Fact]
    public void Create_RejectsItemCountAboveThePolicyLimit()
    {
        var limits = new OrderPricingLimits(9, 3, 100m, 9_999.99m);
        var four = new[] { Item(1m), Item(2m), Item(3m), Item(4m) };

        var exception = Assert.Throws<ArgumentException>(() =>
            Order.Create(Guid.NewGuid(), Guid.NewGuid(), four, limits, Now));
        Assert.Equal("items", exception.ParamName);
        Assert.Contains("more than 3 items", exception.Message);
    }

    [Fact]
    public void Create_AllowsItemCountExactlyAtThePolicyLimit()
    {
        var limits = new OrderPricingLimits(9, 3, 100m, 9_999.99m);
        var three = new[] { Item(1m, 1), Item(2m, 1), Item(3m, 1) };

        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), three, limits, Now);
        Assert.Equal(3, order.Items.Count);
        Assert.Equal(6m, order.TotalAmount);
    }

    [Fact]
    public void Create_RejectsAggregateTotalAboveTheMaximumOrderTotal()
    {

        var limits = new OrderPricingLimits(2, 3, 100m, 599.99m);

        var exception = Assert.Throws<ArgumentException>(() => Order.Create(
            Guid.NewGuid(), Guid.NewGuid(), [Item(100m, 2), Item(100m, 2), Item(100m, 2)], limits, Now));
        Assert.Equal("items", exception.ParamName);
        Assert.Contains("599.99", exception.Message);
    }

    [Fact]
    public void Create_AllowsAggregateTotalExactlyAtTheMaximumOrderTotal()
    {
        var limits = new OrderPricingLimits(2, 3, 100m, 600m);

        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(),
            [Item(100m, 2), Item(100m, 2), Item(100m, 2)], limits, Now);
        Assert.Equal(600m, order.TotalAmount);
        Assert.Equal(3, order.Items.Count);
    }

    [Fact]
    public void Create_AppliesPolicyLimitsPerItem_EvenWhenAnotherItemDefinesTheTotal()
    {
        var limits = new OrderPricingLimits(5, 20, 500m, 100_000m);

        var exception = Assert.Throws<ArgumentException>(() => Order.Create(
            Guid.NewGuid(), Guid.NewGuid(), [Item(1m, 5), Item(600m, 1)], limits, Now));
        Assert.Equal("items", exception.ParamName);
    }

    [Fact]
    public void TotalAmount_EqualsTheSumOfItemLineTotals()
    {
        var items = new[] { Item(12.75m, 3), Item(0.10m, 7), Item(13.37m, 2) };

        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), items, Limits(), Now);

        Assert.Equal(items.Sum(x => x.TotalPrice), order.TotalAmount);
        Assert.Equal(65.69m, order.TotalAmount);
        Assert.Equal(0.10m, order.Items[1].UnitPrice);
    }






    [Fact]
    public void Create_ProducesAPositiveTotal_EvenAtTheSmallestLine()
    {
        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), [Item(0.01m, 1)], Limits(), Now);
        Assert.Equal(0.01m, order.TotalAmount);
    }

    [Fact]
    public void TotalAmount_CanReachDecimalMaximum_WithinSingleItemLimits()
    {
        var limits = SingleItemLimits();

        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), [Item(decimal.MaxValue, 1)], limits, Now);
        Assert.Equal(decimal.MaxValue, order.TotalAmount);
    }




    [Fact]
    public void Create_AggregatesAtTheConfiguredMonetaryMaxima_WithoutOverflow()
    {


        var limits = new OrderPricingLimits(2, 2, 19807040628566084398385987583m, decimal.MaxValue);

        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(),
            [Item(19807040628566084398385987583m, 2), Item(19807040628566084398385987583m, 2)], limits, Now);

        Assert.Equal(decimal.MaxValue - 3m, order.TotalAmount);
        Assert.Equal(2, order.Items.Count);
    }
}
