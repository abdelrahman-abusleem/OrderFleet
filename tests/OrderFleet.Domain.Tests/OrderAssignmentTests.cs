using OrderFleet.Domain.Orders;
using OrderFleet.Domain.Common;
using static OrderFleet.Domain.Tests.OrderTestSupport;

namespace OrderFleet.Domain.Tests;

public class OrderAssignmentTests
{
    [Theory]
    [InlineData(OrderStatus.Created)]
    [InlineData(OrderStatus.Accepted)]
    [InlineData(OrderStatus.Assigned)]
    [InlineData(OrderStatus.PickedUp)]
    [InlineData(OrderStatus.Delivered)]
    [InlineData(OrderStatus.Cancelled)]
    public void Assign_RejectsEmptyCourierWithoutMutation(OrderStatus status)
    {
        var order = InState(status);
        var snapshot = new Snapshot(order);
        var exception = Assert.Throws<ArgumentException>(() => order.Assign(Guid.Empty, Now));
        Assert.Equal("courierId", exception.ParamName);
        snapshot.AssertUnchanged(order);
    }

    [Fact]
    public void Release_ClearsAssignmentAndReturnsToAccepted_PreservingOrderDetails()
    {
        var order = Create();
        order.Accept(Now);
        var beforeAssignment = new Snapshot(order, includeLifecycle: false);
        var assignment = order.Assign(Guid.NewGuid(), Now);

        order.ReleaseAssignment(assignment, Now);

        beforeAssignment.AssertUnchanged(order);
        Assert.Equal(OrderStatus.Accepted, order.Status);
        Assert.Null(order.CourierId);
        Assert.Null(order.AssignmentId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reassign_UsesFreshToken_AndFencesOldAssignmentThroughDelivery(bool sameCourier)
    {
        var order = InState(OrderStatus.Accepted);
        var oldCourier = Guid.NewGuid();
        var oldAssignment = order.Assign(oldCourier, Now);
        order.ReleaseAssignment(oldAssignment, Now);
        var courier = sameCourier ? oldCourier : Guid.NewGuid();
        var assignment = order.Assign(courier, Now);

        Assert.NotEqual(Guid.Empty, assignment);
        Assert.NotEqual(oldAssignment, assignment);
        Assert.Equal(assignment, order.AssignmentId);
        Assert.Equal(courier, order.CourierId);
        var assigned = new Snapshot(order);

        Assert.Throws<DomainException>(() => order.ReleaseAssignment(oldAssignment, Now));
        assigned.AssertUnchanged(order);
        Assert.Throws<DomainException>(() => order.MarkPickedUp(oldCourier, oldAssignment, Now));
        assigned.AssertUnchanged(order);
        Assert.Throws<DomainException>(() => order.MarkPickedUp(courier, oldAssignment, Now));
        assigned.AssertUnchanged(order);
        if (!sameCourier)
        {
            Assert.Throws<DomainException>(() => order.MarkPickedUp(oldCourier, assignment, Now));
            assigned.AssertUnchanged(order);
        }

        order.MarkPickedUp(courier, assignment, Now);
        var pickedUp = new Snapshot(order);
        Assert.Throws<DomainException>(() => order.Deliver(oldCourier, oldAssignment, Now));
        pickedUp.AssertUnchanged(order);
        Assert.Throws<DomainException>(() => order.Deliver(courier, oldAssignment, Now));
        pickedUp.AssertUnchanged(order);
        if (!sameCourier)
        {
            Assert.Throws<DomainException>(() => order.Deliver(oldCourier, assignment, Now));
            pickedUp.AssertUnchanged(order);
        }

        order.Deliver(courier, assignment, Now);
        Assert.Equal(OrderStatus.Delivered, order.Status);
        Assert.Equal(courier, order.CourierId);
        Assert.Equal(assignment, order.AssignmentId);
    }

    public static IEnumerable<object[]> InvalidAssignmentIdentifiers()
    {
        foreach (var operation in new[] { "PickUp", "Deliver" })
            foreach (var courierKind in new[] { "Current", "Empty", "Other" })
                foreach (var assignmentKind in new[] { "Current", "Empty", "Other" })
                {
                    if (courierKind != "Current" || assignmentKind != "Current")
                        yield return [operation, courierKind, assignmentKind];
                }
    }

    [Theory]
    [MemberData(nameof(InvalidAssignmentIdentifiers))]
    public void PickupAndDelivery_RejectEveryInvalidIdentifierPairWithoutMutation(
        string operation, string courierKind, string assignmentKind)
    {
        var order = InState(operation == "PickUp" ? OrderStatus.Assigned : OrderStatus.PickedUp);
        var snapshot = new Snapshot(order);
        var courier = Identifier(courierKind, order.CourierId!.Value);
        var assignment = Identifier(assignmentKind, order.AssignmentId!.Value);

        Assert.Throws<DomainException>(() =>
        {
            if (operation == "PickUp") order.MarkPickedUp(courier, assignment, Now);
            else order.Deliver(courier, assignment, Now);
        });
        snapshot.AssertUnchanged(order);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Release_RejectsEmptyOrMismatchedAssignmentWithoutMutation(bool empty)
    {
        var order = InState(OrderStatus.Assigned);
        var snapshot = new Snapshot(order);
        Assert.Throws<DomainException>(() =>
            order.ReleaseAssignment(empty ? Guid.Empty : Guid.NewGuid(), Now));
        snapshot.AssertUnchanged(order);
    }

    private static Guid Identifier(string kind, Guid current) => kind switch
    {
        "Current" => current,
        "Empty" => Guid.Empty,
        "Other" => Guid.NewGuid(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
