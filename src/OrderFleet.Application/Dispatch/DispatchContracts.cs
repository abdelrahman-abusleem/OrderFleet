namespace OrderFleet.Application.Dispatch;

public enum DispatchOutcome { Assigned = 1, NoCourier, NotEligible, AlreadyProcessed, Exhausted, NotDue }
public enum CourierOrderAction { Pickup = 1, Deliver, Release }
public sealed record DispatchPolicy(double RadiusMeters, int MaxAttempts, TimeSpan PickupTimeout, int MaxAssignments);

public interface IDispatchStore
{
    Task<DispatchOutcome> TryAssignAsync(Guid orderId, Guid? messageId, DateTimeOffset now, DispatchPolicy policy, CancellationToken cancellationToken);
    Task TransitionAsync(Guid orderId, Guid actorId, Guid assignmentId, CourierOrderAction action,
        bool administrativeRelease, DateTimeOffset now, CancellationToken cancellationToken);
}
