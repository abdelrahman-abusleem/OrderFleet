using OrderFleet.Application.Errors;

namespace OrderFleet.Application.Catalog;

public sealed class BrowseCatalog(ICatalogStore store)
{
    public async Task<IReadOnlyList<RestaurantSummary>> RestaurantsAsync(int page, int size, CancellationToken cancellationToken)
    {
        var offset = Offset(page, size);
        var rows = await store.ListRestaurantsAsync(offset, size, cancellationToken);
        return rows.Select(x => new RestaurantSummary(x.Id, x.Name, x.Latitude, x.Longitude)).ToArray();
    }

    public async Task<IReadOnlyList<ProductSummary>> ProductsAsync(Guid restaurantId, int page, int size, CancellationToken cancellationToken)
    {
        var offset = Offset(page, size);
        var rows = await store.ListProductsAsync(restaurantId, offset, size, cancellationToken);
        return rows.Select(x => new ProductSummary(x.Id, x.Name, x.Price, x.Currency, x.IsAvailable)).ToArray();
    }

    public static int Offset(int page, int size)
    {
        if (page is < 1 or > 100000 || size is < 1 or > 50)
            throw new RequestValidationException("Page must be 1..100000 and pageSize 1..50.");
        return (page - 1) * size;
    }
}
