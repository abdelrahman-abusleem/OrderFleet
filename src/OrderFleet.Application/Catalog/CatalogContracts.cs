using OrderFleet.Application.Identity;
using OrderFleet.Domain.Restaurants;

namespace OrderFleet.Application.Catalog;

public interface IRestaurantAccess
{
    Task<bool> HasMembershipAsync(Guid userId, Guid restaurantId, CancellationToken cancellationToken);
}

public interface ICatalogStore
{
    Task<Restaurant?> RestaurantAsync(Guid id, CancellationToken cancellationToken);
    Task<Product?> ProductAsync(Guid restaurantId, Guid productId, CancellationToken cancellationToken);
    Task CreateRestaurantAsync(Restaurant restaurant, Guid ownerId, CancellationToken cancellationToken);
    Task CreateProductAsync(Product product, CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Restaurant>> ListRestaurantsAsync(int offset, int count, CancellationToken cancellationToken);
    Task<IReadOnlyList<Product>> ListProductsAsync(Guid restaurantId, int offset, int count, CancellationToken cancellationToken);
}

public sealed record RestaurantSummary(Guid Id, string Name, decimal Latitude, decimal Longitude);
public sealed record ProductSummary(Guid Id, string Name, decimal Price, string Currency, bool IsAvailable);
