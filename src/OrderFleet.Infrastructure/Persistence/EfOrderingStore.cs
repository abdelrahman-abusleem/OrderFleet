using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Orders;
using OrderFleet.Domain.Orders;
using OrderFleet.Infrastructure.Orders;
using OrderFleet.Domain.Payments;
using OrderFleet.Application.Payments;
using OrderFleet.Application.Messaging;
using OrderFleet.Infrastructure.Messaging;

namespace OrderFleet.Infrastructure.Persistence;

public sealed class EfOrderingStore(OrderFleetDbContext context) : IOrderingStore
{
    public Task<OrderReplay?> FindReplayAsync(Guid actorId, string key, CancellationToken cancellationToken) =>
        context.OrderCreationRequests.AsNoTracking().Where(x => x.ActorId == actorId && x.Key == key)
            .Select(x => new OrderReplay(x.OrderId, x.Fingerprint)).SingleOrDefaultAsync(cancellationToken);

    public async Task<CatalogSnapshot> ReadCatalogAsync(Guid restaurantId, IReadOnlyList<Guid> productIds, CancellationToken cancellationToken)
    {
        var products = await context.Products.AsNoTracking().Where(x => x.RestaurantId == restaurantId
            && productIds.Contains(x.Id) && context.Restaurants.Any(r => r.Id == restaurantId && r.IsActive))
            .ToArrayAsync(cancellationToken);
        return new(products.Length > 0, products);
    }

    public async Task CommitNewAsync(Order order, Payment payment, DeliveryDetails delivery, string key, string fingerprint,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var authorization = await context.MockPaymentAuthorizations.FromSqlInterpolated(
            $"SELECT *, xmin FROM \"MockPaymentAuthorizations\" WHERE \"Id\" = {payment.AuthorizationId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (authorization is null || authorization.State != ProviderPaymentState.Authorized
            || authorization.ActorId != order.CustomerId || authorization.Key != key || authorization.Fingerprint != fingerprint
            || authorization.Amount != order.TotalAmount || authorization.Currency != order.Currency
            || payment.OrderId != order.Id || payment.Amount != order.TotalAmount || payment.Currency != order.Currency)
            throw new OperationConflictException("Payment authorization does not match the order.");
        // Revalidate the trusted snapshot under DB share locks. Catalog mutation cannot
        // slip between this check and commit; lock in stable restaurant/product order.
        var restaurant = await context.Restaurants.FromSqlInterpolated(
            $"SELECT *, xmin FROM \"Restaurants\" WHERE \"Id\" = {order.RestaurantId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var ids = order.Items.Select(x => x.ProductId).OrderBy(x => x).ToArray();
        var current = await context.Products.FromSqlInterpolated(
            $"SELECT *, xmin FROM \"Products\" WHERE \"RestaurantId\" = {order.RestaurantId} AND \"Id\" = ANY({ids}) ORDER BY \"Id\" FOR SHARE")
            .AsNoTracking().ToArrayAsync(cancellationToken);
        if (restaurant is null || !restaurant.IsActive || current.Length != order.Items.Count
            || order.Items.Any(item => !current.Any(product => product.Id == item.ProductId && product.IsAvailable
                && product.Name == item.Name && product.Price == item.UnitPrice)))
            throw new OperationConflictException("Catalog changed; retry the order request.");
        context.Orders.Add(order);
        context.Payments.Add(payment);
        context.OrderDeliveryInformation.Add(new(order.Id, delivery));
        context.OrderCreationRequests.Add(new(order.CustomerId, key, fingerprint, order.Id));
        RecordAudit(order.CustomerId, order.Id, "OrderCreated", order.CreatedAt);
        context.OutboxMessages.Add(new(IntegrationEvents.FromPayment(payment, "payment.authorized.v1", payment.AuthorizedAt)));
        var eventWriter = new OutboxEventWriter(context);
        var staged = eventWriter.StageOrderFacts();
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            OutboxEventWriter.ClearCommitted(staged);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_OrderCreationRequests" or "IX_Payments_AuthorizationId" })
        {
            await transaction.RollbackAsync(cancellationToken);
            context.ChangeTracker.Clear();
            throw new DuplicateRequestException();
        }
    }

    public Task<Order?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        context.Orders.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    public Task<DeliveryDetails?> DeliveryAsync(Guid id, CancellationToken cancellationToken) =>
        context.OrderDeliveryInformation.AsNoTracking().Where(x => x.OrderId == id)
            .Select(x => new DeliveryDetails(x.Latitude, x.Longitude, x.AcceptanceDeadline)).SingleOrDefaultAsync(cancellationToken);
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        await using var ownedTransaction = context.Database.CurrentTransaction is null
            ? await context.Database.BeginTransactionAsync(cancellationToken) : null;
        var roots = context.ChangeTracker.Entries<Order>().Where(x => x.Entity.DomainEvents.Count > 0)
            .OrderBy(x => x.Entity.Id).ToArray();
        foreach (var root in roots.Where(x => x.State != EntityState.Added))
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"Orders\" WHERE \"Id\" = {root.Entity.Id} FOR UPDATE", cancellationToken);
        await SynchronizeOrderTransitionEffectsAsync(roots.Select(x => x.Entity), cancellationToken);
        var staged = new OutboxEventWriter(context).StageOrderFacts();
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_OutboxMessages_AggregateId_AggregateSequence" })
        {
            // EF may insert the event before updating the root's xmin. Two stale
            // writers therefore collide on the event sequence before the root CAS.
            // SaveChanges rolls back both; expose the same optimistic conflict.
            throw new DbUpdateConcurrencyException("Order event sequence changed concurrently.", error);
        }
        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitAsync(cancellationToken);
            OutboxEventWriter.ClearCommitted(staged);
        }
    }

    private async Task SynchronizeOrderTransitionEffectsAsync(IEnumerable<Order> orders, CancellationToken ct)
    {
        foreach (var order in orders)
            foreach (var fact in order.DomainEvents)
            {
                // Preserve per-order/event processing and courier-before-job/history
                // query order within SaveAsync's existing context and transaction.
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
            courier?.Release(orderId, references.Value.AssignmentId);
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
    public void RecordAudit(Guid actorId, Guid orderId, string action, DateTimeOffset occurredAt) =>
        context.OrderAuditEntries.Add(new(actorId, orderId, action, occurredAt));
    public async Task<IReadOnlyList<Order>> ListAsync(Guid? customerId, Guid? restaurantId, int offset, int count,
        CancellationToken cancellationToken) => await context.Orders.AsNoTracking()
            .Where(x => (!customerId.HasValue || x.CustomerId == customerId) && (!restaurantId.HasValue || x.RestaurantId == restaurantId))
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip(offset).Take(count).ToArrayAsync(cancellationToken);
}
