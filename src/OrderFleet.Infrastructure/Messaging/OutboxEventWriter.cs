using Microsoft.EntityFrameworkCore;
using OrderFleet.Application.Messaging;
using OrderFleet.Domain.Orders;
using OrderFleet.Infrastructure.Persistence;

namespace OrderFleet.Infrastructure.Messaging;

internal sealed class OutboxEventWriter(OrderFleetDbContext context)
{
    internal Order[] StageOrderFacts()
    {
        var roots = context.ChangeTracker.Entries<Order>()
            .Where(x => (x.State is EntityState.Added or EntityState.Modified) && x.Entity.DomainEvents.Count != 0)
            .Select(x => x.Entity).ToArray();
        foreach (var order in roots)
        {
            var sequence = context.Entry(order).Property<long>("LastEventSequence");
            foreach (var fact in order.DomainEvents)
            {
                if (!context.OutboxMessages.Local.Any(x => x.Id == fact.EventId))
                {
                    sequence.CurrentValue++;
                    context.OutboxMessages.Add(new(IntegrationEvents.FromOrder(fact) with { AggregateSequence = sequence.CurrentValue }));
                }
            }
        }
        return roots;
    }

    internal static void ClearCommitted(IEnumerable<Order> orders)
    {
        foreach (var order in orders) order.ClearDomainEvents();
    }
}
