using System.Security.Claims;
using OrderFleet.Api.Identity;
using OrderFleet.Application.Catalog;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Identity;

namespace OrderFleet.Api.Endpoints;

public static class CatalogEndpoints
{
    public sealed record RestaurantRequest(string? Name, decimal? Latitude, decimal? Longitude);
    public sealed record ProductRequest(string? Name, decimal Price, bool IsAvailable = true);
    public sealed record AvailabilityRequest(bool? IsActive);

    public static void MapCatalogEndpoints(this WebApplication app)
    {
        var restaurants = app.MapGroup("/api/v1/restaurants");
        restaurants.MapGet("/", async (int? page, int? pageSize, BrowseCatalog query, CancellationToken token) =>
            Results.Ok(await query.RestaurantsAsync(page ?? 1, pageSize ?? 20, token))).AllowAnonymous();
        restaurants.MapGet("/{id:guid}/products", async (Guid id, int? page, int? pageSize, BrowseCatalog query, CancellationToken token) =>
            Results.Ok(await query.ProductsAsync(id, page ?? 1, pageSize ?? 20, token))).AllowAnonymous();
        restaurants.MapPost("/", async (RestaurantRequest request, ClaimsPrincipal principal, ManageCatalog useCase, CancellationToken token) =>
        {
            var id = await useCase.CreateRestaurantAsync(principal.ToActor(), request.Name, request.Latitude, request.Longitude, token);
            return Results.Created($"/api/v1/restaurants/{id}", new { id });
        }).RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.RestaurantOwner));
        restaurants.MapPost("/{id:guid}/products", async (Guid id, ProductRequest request, ClaimsPrincipal principal,
            ManageCatalog useCase, CancellationToken token) =>
        {
            var product = await useCase.CreateProductAsync(principal.ToActor(), id, request.Name, request.Price, request.IsAvailable, token);
            return Results.Created($"/api/v1/restaurants/{id}/products/{product}", new { id = product });
        }).RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.RestaurantOwner, OrderFleetRoles.RestaurantStaff));
        restaurants.MapPut("/{id:guid}/products/{productId:guid}", async (Guid id, Guid productId, ProductRequest request,
            ClaimsPrincipal principal, ManageCatalog useCase, CancellationToken token) =>
        {
            await useCase.UpdateProductAsync(principal.ToActor(), id, productId, request.Name, request.Price, request.IsAvailable, token);
            return Results.NoContent();
        }).RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.RestaurantOwner, OrderFleetRoles.RestaurantStaff));
        restaurants.MapPut("/{id:guid}/availability", async (Guid id, AvailabilityRequest request,
            ClaimsPrincipal principal, ManageCatalog useCase, CancellationToken token) =>
        {
            if (request.IsActive is null) throw new RequestValidationException("isActive is required.");
            await useCase.SetActiveAsync(principal.ToActor(), id, request.IsActive.Value, token);
            return Results.NoContent();
        }).RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.RestaurantOwner, OrderFleetRoles.RestaurantStaff));
    }
}
