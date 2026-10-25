using OrderFleet.Domain.Orders;
using static OrderFleet.Domain.Tests.OrderTestSupport;

namespace OrderFleet.Domain.Tests;

public class OrderStateMachineTests
{
    [Fact]
    public void Lifecycle_TransitionsThroughDelivery_AndPreservesOrderDetails()
    {
        var order = Create();
        var id = order.Id;
        var customer = order.CustomerId;
        var restaurant = order.RestaurantId;
        var items = order.Items.ToArray();
        var total = order.TotalAmount;
        var courier = Guid.NewGuid();

        order.Accept(Now);
        Assert.Equal(OrderStatus.Accepted, order.Status);
        Assert.Null(order.CourierId);
        Assert.Null(order.AssignmentId);

        var assignment = order.Assign(courier, Now);
        Assert.Equal(OrderStatus.Assigned, order.Status);
        Assert.NotEqual(Guid.Empty, assignment);
        Assert.Equal(courier, order.CourierId);
        Assert.Equal(assignment, order.AssignmentId);

        order.MarkPickedUp(courier, assignment, Now);
        Assert.Equal(OrderStatus.PickedUp, order.Status);
        Assert.Equal(courier, order.CourierId);
        Assert.Equal(assignment, order.AssignmentId);

        order.Deliver(courier, assignment, Now);
        Assert.Equal(OrderStatus.Delivered, order.Status);
        Assert.Equal(courier, order.CourierId);
        Assert.Equal(assignment, order.AssignmentId);
        Assert.Null(order.CancelReason);
        Assert.Equal(id, order.Id);
        Assert.Equal(customer, order.CustomerId);
        Assert.Equal(restaurant, order.RestaurantId);
        Assert.Equal(items, order.Items);
        Assert.Equal(total, order.TotalAmount);
        Assert.Equal("SAR", order.Currency);
    }

    public static IEnumerable<object[]> InvalidTransitions()
    {
        foreach (var state in Enum.GetValues<OrderStatus>())
        {
            if (state != OrderStatus.Created) yield return [state, "Accept"];
            if (state != OrderStatus.Accepted) yield return [state, "Assign"];
            if (state != OrderStatus.Assigned) yield return [state, "PickUp"];
            if (state != OrderStatus.PickedUp) yield return [state, "Deliver"];
            if (state != OrderStatus.Assigned) yield return [state, "Release"];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidTransitions))]
    public void Operation_InEveryInvalidState_IsRejectedWithoutMutation(OrderStatus state, string operation)
    {
        var order = InState(state);
        var snapshot = new Snapshot(order);
        var courier = order.CourierId ?? Guid.NewGuid();
        var assignment = order.AssignmentId ?? Guid.NewGuid();

        Action act = operation switch
        {
            "Accept" => () => order.Accept(Now),
            "Assign" => () => order.Assign(courier, Now),
            "PickUp" => () => order.MarkPickedUp(courier, assignment, Now),
            "Deliver" => () => order.Deliver(courier, assignment, Now),
            "Release" => () => order.ReleaseAssignment(assignment, Now),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        // Retrying a rejected operation must also be harmless.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Throws<DomainException>(act);
            snapshot.AssertUnchanged(order);
        }
    }

    [Fact]
    public void Release_CannotBeRepeatedAfterSuccessfulRelease()
    {
        var order = InState(OrderStatus.Assigned);
        var assignment = order.AssignmentId!.Value;
        order.ReleaseAssignment(assignment, Now);
        var snapshot = new Snapshot(order);

        Assert.Throws<DomainException>(() => order.ReleaseAssignment(assignment, Now));
        snapshot.AssertUnchanged(order);
    }
}
