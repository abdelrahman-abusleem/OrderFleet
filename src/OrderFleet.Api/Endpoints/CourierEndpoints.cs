using System.Security.Claims;
using OrderFleet.Api.Identity;
using OrderFleet.Application.Couriers;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Identity;

namespace OrderFleet.Api.Endpoints;

public static class CourierEndpoints
{
    public sealed record AvailabilityRequest(bool? IsAvailable);
    public sealed record LocationRequest(decimal? Latitude, decimal? Longitude, DateTimeOffset? ObservedAt);

    public static void MapCourierEndpoints(this WebApplication app)
    {
        var self = app.MapGroup("/api/v1/couriers/me").RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.Courier));
        self.MapPut("/", async (ClaimsPrincipal principal, ManageCourier command, CancellationToken ct) =>
        {
            await command.RegisterAsync(principal.ToActor(), ct);
            return Results.NoContent();
        });
        self.MapGet("/", async (ClaimsPrincipal principal, ManageCourier query, CancellationToken ct) =>
            Results.Ok(await query.GetAsync(principal.ToActor(), ct)));
        self.MapPut("/availability", async (AvailabilityRequest request, ClaimsPrincipal principal, ManageCourier command, CancellationToken ct) =>
        {
            if (request.IsAvailable is null) throw new RequestValidationException("isAvailable is required.");
            await command.SetAvailabilityAsync(principal.ToActor(), request.IsAvailable.Value, ct);
            return Results.NoContent();
        });
        self.MapPut("/location", async (LocationRequest request, ClaimsPrincipal principal, ManageCourier command, CancellationToken ct) =>
        {
            await command.UpdateLocationAsync(principal.ToActor(), request.Latitude, request.Longitude, request.ObservedAt, ct);
            return Results.NoContent();
        });
        app.MapGet("/api/v1/restaurants/{id:guid}/nearby-couriers", async (Guid id, double? radiusMeters, int? limit,
            ClaimsPrincipal principal, FindNearbyCouriers query, CancellationToken ct) =>
            Results.Ok(await query.ExecuteAsync(principal.ToActor(), id, radiusMeters ?? 5000, limit ?? 20, ct)))
            .RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.Admin, OrderFleetRoles.RestaurantOwner, OrderFleetRoles.RestaurantStaff));
    }
}
