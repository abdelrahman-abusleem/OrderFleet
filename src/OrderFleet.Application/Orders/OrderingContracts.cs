using OrderFleet.Domain.Orders;
using OrderFleet.Domain.Restaurants;

namespace OrderFleet.Application.Orders;

public sealed record OrderingPolicy(OrderPricingLimits Limits, TimeSpan AcceptanceTimeout);
public sealed record RequestedItem(Guid ProductId, int Quantity);
public sealed record CreateOrderRequest(Guid RestaurantId, IReadOnlyList<RequestedItem?>? Items, decimal? Latitude, decimal? Longitude);
public sealed record OrderReplay(Guid OrderId, string Fingerprint);
public sealed record CatalogSnapshot(bool Active, IReadOnlyList<Product> Products);
public sealed record DeliveryDetails(decimal Latitude, decimal Longitude, DateTimeOffset AcceptanceDeadline);

public interface IOrderingStore
{
    Task<OrderReplay?> FindReplayAsync(Guid actorId, string key, CancellationToken cancellationToken);
    Task<CatalogSnapshot> ReadCatalogAsync(Guid restaurantId, IReadOnlyList<Guid> productIds, CancellationToken cancellationToken);
    Task CommitNewAsync(Order order, DeliveryDetails delivery, string key, string fingerprint, CancellationToken cancellationToken);
    Task<Order?> LoadAsync(Guid id, CancellationToken cancellationToken);
    Task<DeliveryDetails?> DeliveryAsync(Guid id, CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
    void RecordAudit(Guid actorId, Guid orderId, string action, DateTimeOffset occurredAt);
    Task<IReadOnlyList<Order>> ListAsync(Guid? customerId, Guid? restaurantId, int offset, int count, CancellationToken cancellationToken);
}
