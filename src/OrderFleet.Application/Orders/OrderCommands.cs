using OrderFleet.Application.Catalog;
using OrderFleet.Application.Clock;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Identity;
using OrderFleet.Domain.Orders;

namespace OrderFleet.Application.Orders;

public sealed class OrderCommands(IOrderingStore store, IRestaurantAccess access, IClock clock)
{
    public async Task AcceptAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        var order = await RestaurantOrderAsync(id, actor, cancellationToken);
        var delivery = await store.DeliveryAsync(id, cancellationToken);
        var now = clock.UtcNow;
        if (delivery is null || now >= delivery.AcceptanceDeadline)
            throw new OperationConflictException("The restaurant acceptance deadline has elapsed.");
        order.Accept(now);
        store.RecordAudit(actor.Id, order.Id, "OrderAccepted", now);
        await store.SaveAsync(cancellationToken);
    }

    public async Task RejectAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        var order = await RestaurantOrderAsync(id, actor, cancellationToken);
        var now = clock.UtcNow;
        order.Cancel(CancellationReason.RestaurantRejected, now);
        store.RecordAudit(actor.Id, order.Id, "OrderRejected", now);
        await store.SaveAsync(cancellationToken);
    }

    public async Task CancelAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        var order = await store.LoadAsync(id, cancellationToken) ?? throw new ResourceNotFoundException();
        var reason = CancellationReason.CustomerRequested;
        if (actor.IsInRole(OrderFleetRoles.Admin)) reason = CancellationReason.AdminCancelled;
        else if (actor.IsInRole(OrderFleetRoles.Customer) && order.CustomerId == actor.Id) { }
        else if ((actor.IsInRole(OrderFleetRoles.RestaurantOwner) || actor.IsInRole(OrderFleetRoles.RestaurantStaff))
            && await access.HasMembershipAsync(actor.Id, order.RestaurantId, cancellationToken))
            reason = CancellationReason.RestaurantCancelled;
        else throw new ResourceNotFoundException();
        var now = clock.UtcNow;
        order.Cancel(reason, now);
        store.RecordAudit(actor.Id, order.Id, "OrderCancelled", now);
        await store.SaveAsync(cancellationToken);
    }

    private async Task<Order> RestaurantOrderAsync(Guid id, Actor actor, CancellationToken cancellationToken)
    {
        if (!actor.IsInRole(OrderFleetRoles.RestaurantOwner) && !actor.IsInRole(OrderFleetRoles.RestaurantStaff))
            throw new AccessDeniedException();
        var order = await store.LoadAsync(id, cancellationToken) ?? throw new ResourceNotFoundException();
        if (!await access.HasMembershipAsync(actor.Id, order.RestaurantId, cancellationToken)) throw new ResourceNotFoundException();
        return order;
    }
}
