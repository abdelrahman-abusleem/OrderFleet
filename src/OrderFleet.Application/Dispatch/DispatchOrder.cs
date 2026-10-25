using OrderFleet.Application.Clock;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Identity;
using OrderFleet.Application.Messaging;

namespace OrderFleet.Application.Dispatch;

public sealed class DispatchOrder(IDispatchStore store, IClock clock, DispatchPolicy policy)
{
    public Task<DispatchOutcome> ExecuteAsync(Guid orderId, Guid? messageId, CancellationToken ct) =>
        store.TryAssignAsync(orderId, messageId, clock.UtcNow, policy, ct);
}

public sealed class DispatchEventHandler(DispatchOrder dispatch) : IIntegrationEventHandler
{
    public async Task HandleAsync(IntegrationEvent message, CancellationToken cancellationToken)
    {
        if (message.Type is not ("order.accepted.v1" or "order.assignment-released.v1") || message.Version != 1)
            throw new InvalidDataException("Unsupported dispatch event.");
        await dispatch.ExecuteAsync(message.AggregateId, message.MessageId, cancellationToken);
    }
}

public sealed class CourierOrderCommands(IDispatchStore store, IClock clock)
{
    public Task ExecuteAsync(Guid orderId, Actor actor, Guid assignmentId, CourierOrderAction action, CancellationToken cancellationToken)
    {
        var administrativeRelease = action == CourierOrderAction.Release && actor.IsInRole(OrderFleetRoles.Admin);
        if (!administrativeRelease && !actor.IsInRole(OrderFleetRoles.Courier)) throw new AccessDeniedException();
        if (assignmentId == Guid.Empty) throw new RequestValidationException("Current assignmentId is required.");
        return store.TransitionAsync(orderId, actor.Id, assignmentId, action, administrativeRelease, clock.UtcNow, cancellationToken);
    }
}
