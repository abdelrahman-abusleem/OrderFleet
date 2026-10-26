using OrderFleet.Domain.Couriers;
using OrderFleet.Domain.Orders;

namespace OrderFleet.Domain.Tests;

public sealed class CourierTests
{
    [Fact]
    public void IdenticalObservationReplay_IsNoOp()
    {
        var courier = Courier.Register(Guid.NewGuid());
        var observed = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        courier.UpdateLocation(new(24.7m, 46.7m), observed);
        courier.UpdateLocation(new(24.7m, 46.7m), observed);
        Assert.Equal(observed, courier.LocationObservedAt);
        Assert.Equal(24.7m, courier.Latitude);
        Assert.Equal(46.7m, courier.Longitude);
        Assert.Equal(CourierStatus.Offline, courier.Status);
        Assert.Null(courier.AssignmentId);
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Location_IsStrictlyOrdered_AndRejectedWritePreservesProfile()
    {
        var id = Guid.NewGuid();
        var courier = Courier.Register(id);
        Assert.Equal(id, courier.Id);
        Assert.Equal(CourierStatus.Offline, courier.Status);
        Assert.Null(courier.LocationObservedAt);
        courier.UpdateLocation(new(24.7m, 46.7m), Now);
        Assert.Throws<DomainException>(() => courier.UpdateLocation(new(0, 0), Now));
        Assert.Throws<DomainException>(() => courier.UpdateLocation(new(0, 0), Now.AddTicks(-1)));
        Assert.Equal(24.7m, courier.Latitude);
        Assert.Equal(46.7m, courier.Longitude);
        Assert.Equal(Now, courier.LocationObservedAt);
        courier.UpdateLocation(new(0, 0), Now.AddTicks(1));
        Assert.Equal(0m, courier.Latitude);
    }

    [Fact]
    public void ReservationAndBusyState_AreFenced_AndCannotBeOverriddenByAvailability()
    {
        var courier = Courier.Register(Guid.NewGuid());
        var order = Guid.NewGuid();
        var assignment = Guid.NewGuid();
        Assert.Throws<DomainException>(() => courier.Reserve(order, assignment));
        courier.SetAvailable(true);
        Assert.Throws<ArgumentException>(() => courier.Reserve(Guid.Empty, assignment));
        courier.Reserve(order, assignment);
        Assert.Equal(CourierStatus.Reserved, courier.Status);
        Assert.Throws<DomainException>(() => courier.SetAvailable(false));
        Assert.Throws<DomainException>(() => courier.MarkBusy(order, Guid.NewGuid()));
        Assert.Throws<DomainException>(() => courier.Release(order, Guid.NewGuid()));
        Assert.Equal(assignment, courier.AssignmentId);
        courier.MarkBusy(order, assignment);
        Assert.Equal(CourierStatus.Busy, courier.Status);
        Assert.Throws<DomainException>(() => courier.SetAvailable(true));
        courier.Release(order, assignment);
        Assert.Equal(CourierStatus.Available, courier.Status);
        Assert.Null(courier.OrderId);
        Assert.Null(courier.AssignmentId);
        courier.SetAvailable(false);
        Assert.Equal(CourierStatus.Offline, courier.Status);
    }

    [Fact]
    public void Registration_RequiresIdentity() => Assert.Throws<ArgumentException>(() => Courier.Register(Guid.Empty));
}
