using Microsoft.EntityFrameworkCore;
using OrderFleet.Domain.Orders;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class OrderConcurrencyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(OrderStatus.Created, "Accept", "Cancel")]
    [InlineData(OrderStatus.Created, "Cancel", "Accept")]
    [InlineData(OrderStatus.Accepted, "Assign", "Cancel")]
    [InlineData(OrderStatus.Accepted, "Cancel", "Assign")]
    [InlineData(OrderStatus.Accepted, "Assign", "Assign")]
    [InlineData(OrderStatus.Assigned, "Release", "PickUp")]
    [InlineData(OrderStatus.Assigned, "PickUp", "Release")]
    [InlineData(OrderStatus.Assigned, "Cancel", "PickUp")]
    [InlineData(OrderStatus.Assigned, "PickUp", "Cancel")]
    public async Task StaleContext_CannotOverwriteCommittedTransition(
        OrderStatus state, string firstOperation, string staleOperation)
    {
        var id = await SeedAsync(state);
        await using var firstContext = fixture.CreateContext();
        await using var staleContext = fixture.CreateContext();
        var first = await firstContext.Orders.SingleAsync(x => x.Id == id);
        var stale = await staleContext.Orders.SingleAsync(x => x.Id == id);
        var initialVersion = firstContext.Entry(first).Property<uint>("xmin").CurrentValue;
        Assert.NotEqual(0u, initialVersion);
        Transition(first, firstOperation);
        Transition(stale, staleOperation);
        await firstContext.SaveChangesAsync();

        Assert.NotEqual(initialVersion, firstContext.Entry(first).Property<uint>("xmin").CurrentValue);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleContext.SaveChangesAsync());
        await AssertPersistedAsync(first);
    }

    [Fact]
    public async Task ParallelSaves_CommitExactlyOneConflictingTransition()
    {
        var id = await SeedAsync(OrderStatus.Created);
        await using var a = fixture.CreateContext();
        await using var b = fixture.CreateContext();
        var accepted = await a.Orders.SingleAsync(x => x.Id == id);
        var cancelled = await b.Orders.SingleAsync(x => x.Id == id);
        accepted.Accept(Now.AddMinutes(1));
        cancelled.Cancel(CancellationReason.AdminCancelled, Now.AddMinutes(1));

        var outcomes = await Task.WhenAll(SaveOrConflictAsync(a), SaveOrConflictAsync(b));

        Assert.Single(outcomes, success => success);
        Assert.Single(outcomes, success => !success);
        await AssertPersistedAsync(outcomes[0] ? accepted : cancelled);
    }

    [Fact]
    public async Task StalePickup_CannotResurrectOldFence_AfterSameCourierReassignment()
    {
        var id = await SeedAsync(OrderStatus.Assigned);
        await using var staleContext = fixture.CreateContext();
        var stale = await staleContext.Orders.SingleAsync(x => x.Id == id);
        var oldAssignment = stale.AssignmentId!.Value;
        var courier = stale.CourierId!.Value;
        Guid currentAssignment;
        await using (var currentContext = fixture.CreateContext())
        {
            var current = await currentContext.Orders.SingleAsync(x => x.Id == id);
            current.ReleaseAssignment(oldAssignment, Now.AddMinutes(1));
            currentAssignment = current.Assign(courier, Now.AddMinutes(2));
            await currentContext.SaveChangesAsync();
        }
        // The old in-memory aggregate still considers the old fence valid.
        stale.MarkPickedUp(courier, oldAssignment, Now.AddMinutes(3));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleContext.SaveChangesAsync());
        await using var verify = fixture.CreateContext();
        var actual = await verify.Orders.SingleAsync(x => x.Id == id);
        Assert.Equal(OrderStatus.Assigned, actual.Status);
        Assert.Equal(currentAssignment, actual.AssignmentId);
        Assert.NotEqual(oldAssignment, actual.AssignmentId);
        Assert.Equal(courier, actual.CourierId);
        Assert.Null(actual.PickedUpAt);
    }

    private async Task<Guid> SeedAsync(OrderStatus state)
    {
        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(),
            [new(Guid.NewGuid(), "Tea", 3.50m, 2)], new(99, 20, 9999.99m, 99999.99m), Now);
        if (state != OrderStatus.Created) order.Accept(Now);
        if (state == OrderStatus.Assigned) order.Assign(Guid.NewGuid(), Now);
        await using var context = fixture.CreateContext();
        context.Orders.Add(order);
        await context.SaveChangesAsync();
        return order.Id;
    }

    private static void Transition(Order order, string operation)
    {
        switch (operation)
        {
            case "Accept": order.Accept(Now.AddMinutes(1)); break;
            case "Cancel": order.Cancel(CancellationReason.AdminCancelled, Now.AddMinutes(1)); break;
            case "Assign": order.Assign(Guid.NewGuid(), Now.AddMinutes(1)); break;
            case "Release": order.ReleaseAssignment(order.AssignmentId!.Value, Now.AddMinutes(1)); break;
            case "PickUp": order.MarkPickedUp(order.CourierId!.Value, order.AssignmentId!.Value, Now.AddMinutes(1)); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static async Task<bool> SaveOrConflictAsync(DbContext context)
    {
        try { await context.SaveChangesAsync(); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
    }

    private async Task AssertPersistedAsync(Order expected)
    {
        await using var verify = fixture.CreateContext();
        var actual = await verify.Orders.SingleAsync(x => x.Id == expected.Id);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.CourierId, actual.CourierId);
        Assert.Equal(expected.AssignmentId, actual.AssignmentId);
        Assert.Equal(expected.CancelReason, actual.CancelReason);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.AcceptedAt, actual.AcceptedAt);
        Assert.Equal(expected.AssignedAt, actual.AssignedAt);
        Assert.Equal(expected.PickedUpAt, actual.PickedUpAt);
        Assert.Equal(expected.CancelledAt, actual.CancelledAt);
        Assert.Equal(expected.TotalAmount, actual.TotalAmount);
        Assert.Single(actual.Items);
    }
}
