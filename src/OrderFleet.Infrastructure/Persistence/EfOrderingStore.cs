using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Orders;
using OrderFleet.Domain.Orders;
using OrderFleet.Infrastructure.Orders;

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

    public async Task CommitNewAsync(Order order, DeliveryDetails delivery, string key, string fingerprint,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
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
        context.OrderDeliveryInformation.Add(new(order.Id, delivery));
        context.OrderCreationRequests.Add(new(order.CustomerId, key, fingerprint, order.Id));
        RecordAudit(order.CustomerId, order.Id, "OrderCreated", order.CreatedAt);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_OrderCreationRequests" })
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
    public async Task SaveAsync(CancellationToken cancellationToken) => await context.SaveChangesAsync(cancellationToken);
    public void RecordAudit(Guid actorId, Guid orderId, string action, DateTimeOffset occurredAt) =>
        context.OrderAuditEntries.Add(new(actorId, orderId, action, occurredAt));
    public async Task<IReadOnlyList<Order>> ListAsync(Guid? customerId, Guid? restaurantId, int offset, int count,
        CancellationToken cancellationToken) => await context.Orders.AsNoTracking()
            .Where(x => (!customerId.HasValue || x.CustomerId == customerId) && (!restaurantId.HasValue || x.RestaurantId == restaurantId))
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip(offset).Take(count).ToArrayAsync(cancellationToken);
}
