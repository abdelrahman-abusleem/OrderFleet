using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using OrderFleet.Domain.Orders;

namespace OrderFleet.Infrastructure.Persistence;

// Shares SaveAsync's context and transaction. Stages effects only: never saves,
// commits, clears events or reorders pending facts across aggregate roots.
internal sealed class OrderTransitionEffects(OrderFleetDbContext context, ILogger<EfOrderingStore>? logger)
{
    internal async Task SynchronizeAsync(IEnumerable<Order> orders, CancellationToken ct)
    {
        foreach (var order in orders)
            foreach (var fact in order.DomainEvents)
            {
                // Courier-before-job/history is a concurrency contract.
                await ReleaseCourierCapacityAsync(order.Id, fact, ct);
                await SynchronizeDispatchJobAsync(order.Id, fact, ct);
                await SynchronizeAssignmentHistoryAsync(order.Id, fact, ct);
            }
    }

    private async Task ReleaseCourierCapacityAsync(Guid orderId, OrderDomainEvent fact, CancellationToken ct)
    {
        (Guid CourierId, Guid AssignmentId)? references = fact switch
        {
            AssignmentReleased x => (x.PreviousCourierId, x.PreviousAssignmentId),
            OrderCancelled { PreviousCourierId: not null, PreviousAssignmentId: not null } x => (x.PreviousCourierId.Value, x.PreviousAssignmentId.Value),
            OrderDelivered x => (x.CourierId, x.AssignmentId),
            _ => null
        };
        if (references.HasValue)
        {
            var courier = await context.Couriers.FromSqlInterpolated(
                $"SELECT *, xmin FROM \"Couriers\" WHERE \"Id\" = {references.Value.CourierId} FOR UPDATE").SingleOrDefaultAsync(ct);
            // Standalone legacy Order persistence does not require a Courier
            // profile. Application dispatch/transition paths always require it.
            if (courier is null)
                (logger ?? context.GetService<ILoggerFactory>().CreateLogger<EfOrderingStore>())
                    .LogWarning("Courier capacity missing for order {OrderId}", orderId);
            else courier.Release(orderId, references.Value.AssignmentId);
        }
    }

    private async Task SynchronizeDispatchJobAsync(Guid orderId, OrderDomainEvent fact, CancellationToken ct)
    {
        if (fact is AssignmentReleased or OrderCancelled or OrderDelivered)
        {
            var job = await context.DispatchJobs.SingleOrDefaultAsync(x => x.OrderId == orderId, ct);
            if (fact is AssignmentReleased) job?.Restart(fact.OccurredAt.ToUniversalTime());
            else job?.Finish(fact.OccurredAt.ToUniversalTime());
        }
    }

    private async Task SynchronizeAssignmentHistoryAsync(Guid orderId, OrderDomainEvent fact, CancellationToken ct)
    {
        var assignmentId = fact switch
        {
            OrderPickedUp x => x.AssignmentId,
            AssignmentReleased x => x.PreviousAssignmentId,
            OrderCancelled x => x.PreviousAssignmentId,
            OrderDelivered x => x.AssignmentId,
            _ => (Guid?)null
        };
        if (assignmentId.HasValue)
        {
            var assignment = await context.CourierAssignments.SingleOrDefaultAsync(x => x.Id == assignmentId && x.OrderId == orderId, ct);
            if (fact is OrderPickedUp) assignment?.RecordPickup(fact.OccurredAt.ToUniversalTime());
            else if (fact is OrderDelivered) assignment?.RecordDelivery(fact.OccurredAt.ToUniversalTime());
            else assignment?.RecordRelease(fact.OccurredAt.ToUniversalTime());
        }
    }
}
