using OrderFleet.Application.Errors;
using OrderFleet.Application.Identity;
using OrderFleet.Domain.Geo;
using OrderFleet.Domain.Restaurants;
using OrderFleet.Application.Orders;

namespace OrderFleet.Application.Catalog;

public sealed class ManageCatalog(ICatalogStore store, IRestaurantAccess access, OrderingPolicy policy)
{
    public async Task<Guid> CreateRestaurantAsync(Actor actor, string? name, decimal? latitude, decimal? longitude,
        CancellationToken cancellationToken)
    {
        if (!actor.IsInRole(OrderFleetRoles.RestaurantOwner)) throw new AccessDeniedException();
        ValidateName(name);
        var location = ValidateCoordinates(latitude, longitude);
        var restaurant = Restaurant.Create(name!, location);
        await store.CreateRestaurantAsync(restaurant, actor.Id, cancellationToken);
        return restaurant.Id;
    }

    public async Task<Guid> CreateProductAsync(Actor actor, Guid restaurantId, string? name, decimal price, bool available,
        CancellationToken cancellationToken)
    {
        await AuthorizeAsync(actor, restaurantId, cancellationToken);
        ValidateProduct(name, price);
        var product = Product.Create(restaurantId, name!, price);
        product.Update(name!, price, available);
        await store.CreateProductAsync(product, cancellationToken);
        return product.Id;
    }

    public async Task UpdateProductAsync(Actor actor, Guid restaurantId, Guid productId, string? name,
        decimal price, bool available, CancellationToken cancellationToken)
    {
        await AuthorizeAsync(actor, restaurantId, cancellationToken);
        ValidateProduct(name, price);
        var product = await store.ProductAsync(restaurantId, productId, cancellationToken)
            ?? throw new ResourceNotFoundException();
        product.Update(name!, price, available);
        await store.SaveAsync(cancellationToken);
    }

    public async Task SetActiveAsync(Actor actor, Guid restaurantId, bool active, CancellationToken cancellationToken)
    {
        await AuthorizeAsync(actor, restaurantId, cancellationToken);
        var restaurant = await store.RestaurantAsync(restaurantId, cancellationToken)
            ?? throw new ResourceNotFoundException();
        restaurant.SetActive(active);
        await store.SaveAsync(cancellationToken);
    }

    private async Task AuthorizeAsync(Actor actor, Guid restaurantId, CancellationToken cancellationToken)
    {
        if (!actor.IsInRole(OrderFleetRoles.RestaurantOwner) && !actor.IsInRole(OrderFleetRoles.RestaurantStaff))
            throw new AccessDeniedException();
        if (!await access.HasMembershipAsync(actor.Id, restaurantId, cancellationToken)) throw new ResourceNotFoundException();
    }

    internal static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            throw new RequestValidationException("Name is required and limited to 200 characters.");
    }

    internal static Coordinates ValidateCoordinates(decimal? latitude, decimal? longitude)
    {
        if (latitude is null or < -90 or > 90 || longitude is null or < -180 or > 180)
            throw new RequestValidationException("Valid latitude and longitude are required.");
        return new(latitude.Value, longitude.Value);
    }

    private void ValidateProduct(string? name, decimal price)
    {
        ValidateName(name);
        if (price <= 0 || decimal.Round(price, 2) != price || price > policy.Limits.MaxUnitPrice)
            throw new RequestValidationException("Price must be positive, within the configured maximum, with at most two decimal places.");
    }
}
