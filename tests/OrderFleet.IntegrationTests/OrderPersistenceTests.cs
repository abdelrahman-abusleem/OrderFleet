using Microsoft.EntityFrameworkCore;
using OrderFleet.Domain.Orders;
using OrderFleet.Domain.Common;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class OrderPersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    private static OrderPricingLimits Limits => new(99, 20, 9999.99m, 99999.99m);

    private static Order Create() => Order.Create(Guid.NewGuid(), Guid.NewGuid(),
        [new(Guid.NewGuid(), "Chicken wrap", 12.75m, 3),
         new(Guid.NewGuid(), "Tea", 0.10m, 7)], Limits, Now);

    [Fact]
    public async Task SuppliedOffsetTimestamps_PersistAsTheSameUtcInstant()
    {
        var offsetTime = Now.ToOffset(TimeSpan.FromHours(3));
        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(),
            [new(Guid.NewGuid(), "Tea", 1m, 1)], Limits, offsetTime);
        order.Accept(offsetTime.AddMinutes(1));
        var courier = Guid.NewGuid();
        var assignment = order.Assign(courier, offsetTime.AddMinutes(2));
        order.MarkPickedUp(courier, assignment, offsetTime.AddMinutes(3));
        order.Deliver(courier, assignment, offsetTime.AddMinutes(4));
        await SaveNewAsync(order);
        var loaded = await ReloadAsync(order.Id);
        Assert.Equal(Now, loaded.CreatedAt);
        Assert.Equal(TimeSpan.Zero, loaded.CreatedAt.Offset);
        Assert.Equal(Now.AddMinutes(1), loaded.AcceptedAt);
        Assert.Equal(Now.AddMinutes(2), loaded.AssignedAt);
        Assert.Equal(Now.AddMinutes(3), loaded.PickedUpAt);
        Assert.Equal(Now.AddMinutes(4), loaded.DeliveredAt);

        var cancelled = Order.Create(Guid.NewGuid(), Guid.NewGuid(),
            [new(Guid.NewGuid(), "Tea", 1m, 1)], Limits, offsetTime);
        cancelled.Cancel(CancellationReason.CustomerRequested, offsetTime.AddMinutes(1));
        await SaveNewAsync(cancelled);
        Assert.Equal(Now.AddMinutes(1), (await ReloadAsync(cancelled.Id)).CancelledAt);
    }

    [Fact]
    public async Task MoneyChecks_RejectInvalidDatabaseWrites_WithoutChangingAggregate()
    {
        var order = Create();
        await SaveNewAsync(order);


        foreach (var invalid in new[] { "0", "-1", "0.001", "79228162514264337593543950336" })
        {
            await using var context = fixture.CreateContext();
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"Orders\" SET \"TotalAmount\" = CAST({invalid} AS numeric) WHERE \"Id\" = {order.Id}"));
            Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, error.SqlState);
            var itemPriceError = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"OrderItems\" SET \"UnitPrice\" = CAST({invalid} AS numeric) WHERE \"OrderId\" = {order.Id}"));
            Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, itemPriceError.SqlState);
            var itemTotalError = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"OrderItems\" SET \"TotalPrice\" = CAST({invalid} AS numeric) WHERE \"OrderId\" = {order.Id}"));
            Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, itemTotalError.SqlState);
        }
        var loaded = await ReloadAsync(order.Id);
        Assert.Equal(order.TotalAmount, loaded.TotalAmount);
        Assert.Equal(order.Items.Sum(x => x.TotalPrice), loaded.Items.Sum(x => x.TotalPrice));
    }

    [Fact]
    public async Task DuplicateAggregateInsert_IsRejectedAtomically()
    {
        var order = Create();
        await SaveNewAsync(order);
        var duplicate = await ReloadAsync(order.Id);
        await using (var context = fixture.CreateContext())
        {
            context.Orders.Add(duplicate);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation,
                Assert.IsType<Npgsql.PostgresException>(error.InnerException).SqlState);
        }
        var loaded = await ReloadAsync(order.Id);
        Assert.Equal(order.TotalAmount, loaded.TotalAmount);
        Assert.Equal(2, loaded.Items.Count);
        Assert.Equal(OrderStatus.Created, loaded.Status);
    }

    [Fact]
    public async Task Migrations_AreApplied_AndModelMatchesSnapshot()
    {
        await using var context = fixture.CreateContext();
        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Contains("20261024133928_InitialCreate", applied);
        Assert.True(applied.Length >= 2);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task SaveAndReload_PreservesWholeAggregate_WithoutPersistingTransientEvents()
    {
        var order = Create();
        await SaveNewAsync(order);
        var loaded = await ReloadAsync(order.Id);

        Assert.Equal(order.Id, loaded.Id);
        Assert.Equal(order.CustomerId, loaded.CustomerId);
        Assert.Equal(order.RestaurantId, loaded.RestaurantId);
        Assert.Equal("SAR", loaded.Currency);
        Assert.Equal(38.95m, loaded.TotalAmount);
        Assert.Equal(OrderStatus.Created, loaded.Status);
        Assert.Equal(Now, loaded.CreatedAt);
        Assert.Null(loaded.AcceptedAt);
        Assert.Null(loaded.AssignedAt);
        Assert.Null(loaded.PickedUpAt);
        Assert.Null(loaded.DeliveredAt);
        Assert.Null(loaded.CancelledAt);
        Assert.Null(loaded.CourierId);
        Assert.Null(loaded.AssignmentId);
        Assert.Null(loaded.CancelReason);
        Assert.Empty(loaded.DomainEvents);
        Assert.Equal(2, loaded.Items.Count);
        foreach (var expected in order.Items)
        {
            var actual = Assert.Single(loaded.Items, x => x.ProductId == expected.ProductId);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.UnitPrice, actual.UnitPrice);
            Assert.Equal(expected.Quantity, actual.Quantity);
            Assert.Equal(expected.TotalPrice, actual.TotalPrice);
        }
    }

    [Fact]
    public async Task Lifecycle_AcceptAssignPickupDeliver_PersistsAcrossFreshContexts()
    {
        var order = Create();
        await SaveNewAsync(order);
        await MutateAsync(order.Id, x => x.Accept(Now.AddMinutes(1)));
        var accepted = await ReloadAsync(order.Id);
        Assert.Equal(OrderStatus.Accepted, accepted.Status);
        Assert.Equal(Now.AddMinutes(1), accepted.AcceptedAt);

        var courier = Guid.NewGuid();
        Guid assignment = Guid.Empty;
        await MutateAsync(order.Id, x => assignment = x.Assign(courier, Now.AddMinutes(2)));
        var assigned = await ReloadAsync(order.Id);
        Assert.Equal(OrderStatus.Assigned, assigned.Status);
        Assert.Equal(courier, assigned.CourierId);
        Assert.Equal(assignment, assigned.AssignmentId);
        Assert.Equal(Now.AddMinutes(2), assigned.AssignedAt);

        await MutateAsync(order.Id, x => x.MarkPickedUp(courier, assignment, Now.AddMinutes(3)));
        var pickedUp = await ReloadAsync(order.Id);
        Assert.Equal(OrderStatus.PickedUp, pickedUp.Status);
        Assert.Equal(Now.AddMinutes(3), pickedUp.PickedUpAt);
        await MutateAsync(order.Id, x => x.Deliver(courier, assignment, Now.AddMinutes(4)));
        var delivered = await ReloadAsync(order.Id);
        Assert.Equal(OrderStatus.Delivered, delivered.Status);
        Assert.Equal(Now.AddMinutes(4), delivered.DeliveredAt);
        Assert.Equal(courier, delivered.CourierId);
        Assert.Equal(assignment, delivered.AssignmentId);
        Assert.Equal(order.TotalAmount, delivered.TotalAmount);
        Assert.Equal(2, delivered.Items.Count);
    }

    [Fact]
    public async Task ReleaseAndReassign_PersistNulls_AndFenceOldAssignmentAfterReload()
    {
        var order = Create();
        order.Accept(Now);
        var courier = Guid.NewGuid();
        var oldAssignment = order.Assign(courier, Now);
        await SaveNewAsync(order);
        await MutateAsync(order.Id, x => x.ReleaseAssignment(oldAssignment, Now.AddMinutes(1)));
        var released = await ReloadAsync(order.Id);
        Assert.Equal(OrderStatus.Accepted, released.Status);
        Assert.Null(released.CourierId);
        Assert.Null(released.AssignmentId);

        Guid current = Guid.Empty;
        await MutateAsync(order.Id, x => current = x.Assign(courier, Now.AddMinutes(2)));
        Assert.NotEqual(oldAssignment, current);
        await using (var context = fixture.CreateContext())
        {
            var loaded = await context.Orders.SingleAsync(x => x.Id == order.Id);
            Assert.Throws<DomainException>(() => loaded.ReleaseAssignment(oldAssignment, Now));
            Assert.Throws<DomainException>(() => loaded.MarkPickedUp(courier, oldAssignment, Now));
            Assert.Equal(0, await context.SaveChangesAsync());
        }
        var unchanged = await ReloadAsync(order.Id);
        Assert.Equal(OrderStatus.Assigned, unchanged.Status);
        Assert.Equal(current, unchanged.AssignmentId);
        Assert.Equal(courier, unchanged.CourierId);
    }

    [Theory]
    [InlineData(OrderStatus.Created, CancellationReason.CustomerRequested)]
    [InlineData(OrderStatus.Created, CancellationReason.RestaurantRejected)]
    [InlineData(OrderStatus.Created, CancellationReason.AcceptanceTimedOut)]
    [InlineData(OrderStatus.Created, CancellationReason.AdminCancelled)]
    [InlineData(OrderStatus.Accepted, CancellationReason.RestaurantCancelled)]
    [InlineData(OrderStatus.Accepted, CancellationReason.AdminCancelled)]
    [InlineData(OrderStatus.Assigned, CancellationReason.AdminCancelled)]
    public async Task Cancel_AllAllowedPaths_PersistReasonTimeAndClearedAssignment(
        OrderStatus state, CancellationReason reason)
    {
        var order = Create();
        if (state != OrderStatus.Created) order.Accept(Now);
        if (state == OrderStatus.Assigned) order.Assign(Guid.NewGuid(), Now);
        await SaveNewAsync(order);
        await MutateAsync(order.Id, x => x.Cancel(reason, Now.AddMinutes(1)));

        var cancelled = await ReloadAsync(order.Id);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.Equal(reason, cancelled.CancelReason);
        Assert.Equal(Now.AddMinutes(1), cancelled.CancelledAt);
        Assert.Null(cancelled.CourierId);
        Assert.Null(cancelled.AssignmentId);
        Assert.Empty(cancelled.DomainEvents);
        Assert.Equal(order.TotalAmount, cancelled.TotalAmount);
    }

    [Theory]
    [InlineData("0.01")]
    [InlineData("9999999999999999.99")]
    [InlineData("10000000000000000.01")]
    [InlineData("79228162514264337593543950335")]
    public async Task Money_EntireAdmittedDomainRange_RoundTripsExactly(string priceText)
    {
        var price = decimal.Parse(priceText, System.Globalization.CultureInfo.InvariantCulture);
        var limits = new OrderPricingLimits(1, 1, decimal.MaxValue, decimal.MaxValue);
        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(),
            [new(Guid.NewGuid(), "Boundary", price, 1)], limits, Now);
        await SaveNewAsync(order);
        var loaded = await ReloadAsync(order.Id);
        Assert.Equal(price, loaded.TotalAmount);
        var item = Assert.Single(loaded.Items);
        Assert.Equal(price, item.UnitPrice);
        Assert.Equal(price, item.TotalPrice);
    }

    private async Task SaveNewAsync(Order order)
    {
        await using var context = fixture.CreateContext();
        context.Orders.Add(order);
        await context.SaveChangesAsync();
    }

    private async Task MutateAsync(Guid id, Action<Order> mutate)
    {
        await using var context = fixture.CreateContext();
        var order = await context.Orders.SingleAsync(x => x.Id == id);
        mutate(order);
        await context.SaveChangesAsync();
    }

    private async Task<Order> ReloadAsync(Guid id)
    {
        await using var context = fixture.CreateContext();
        return await context.Orders.AsNoTracking().SingleAsync(x => x.Id == id);
    }
}
