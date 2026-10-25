using Microsoft.EntityFrameworkCore;
using OrderFleet.Application.Catalog;
using OrderFleet.Domain.Restaurants;
using OrderFleet.Infrastructure.Identity;

namespace OrderFleet.Infrastructure.Persistence;

public sealed class EfCatalogStore(OrderFleetDbContext context) : ICatalogStore, IRestaurantAccess
{
    public Task<Restaurant?> RestaurantAsync(Guid id, CancellationToken cancellationToken) =>
        context.Restaurants.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    public Task<Product?> ProductAsync(Guid restaurantId, Guid productId, CancellationToken cancellationToken) =>
        context.Products.SingleOrDefaultAsync(x => x.Id == productId && x.RestaurantId == restaurantId, cancellationToken);
    public Task<bool> HasMembershipAsync(Guid userId, Guid restaurantId, CancellationToken cancellationToken) =>
        context.RestaurantMemberships.AnyAsync(x => x.UserId == userId && x.RestaurantId == restaurantId, cancellationToken);

    public async Task CreateRestaurantAsync(Restaurant restaurant, Guid ownerId, CancellationToken cancellationToken)
    {
        context.Restaurants.Add(restaurant);
        context.RestaurantMemberships.Add(new RestaurantMembership(ownerId, restaurant.Id));
        await context.SaveChangesAsync(cancellationToken);
    }
    public async Task CreateProductAsync(Product product, CancellationToken cancellationToken)
    {
        context.Products.Add(product);
        await context.SaveChangesAsync(cancellationToken);
    }
    public async Task SaveAsync(CancellationToken cancellationToken) => await context.SaveChangesAsync(cancellationToken);
    public async Task<IReadOnlyList<Restaurant>> ListRestaurantsAsync(int offset, int count, CancellationToken cancellationToken) =>
        await context.Restaurants.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Id)
            .Skip(offset).Take(count).ToArrayAsync(cancellationToken);
    public async Task<IReadOnlyList<Product>> ListProductsAsync(Guid restaurantId, int offset, int count, CancellationToken cancellationToken) =>
        await context.Products.AsNoTracking().Where(x => x.RestaurantId == restaurantId && x.IsAvailable
            && context.Restaurants.Any(r => r.Id == restaurantId && r.IsActive)).OrderBy(x => x.Id)
            .Skip(offset).Take(count).ToArrayAsync(cancellationToken);
}
