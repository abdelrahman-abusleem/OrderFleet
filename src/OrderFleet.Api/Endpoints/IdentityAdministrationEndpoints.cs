using OrderFleet.Api.Identity;
using OrderFleet.Application.Identity;

namespace OrderFleet.Api.Endpoints;

public static class IdentityAdministrationEndpoints
{
    public static void MapIdentityAdministrationEndpoints(this WebApplication app)
    {
        var roles = app.MapGroup("/api/v1/identity/users/{userId:guid}/roles")
            .RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.Admin));
        roles.MapPut("/{role}", async (Guid userId, string role, HttpContext context, ManageIdentityAccess command, CancellationToken ct) =>
        { await command.RoleAsync(context.User.ToActor(), userId, role, true, ct); return Results.NoContent(); });
        roles.MapDelete("/{role}", async (Guid userId, string role, HttpContext context, ManageIdentityAccess command, CancellationToken ct) =>
        { await command.RoleAsync(context.User.ToActor(), userId, role, false, ct); return Results.NoContent(); });
        var memberships = app.MapGroup("/api/v1/restaurants/{restaurantId:guid}/memberships")
            .RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.RestaurantOwner));
        memberships.MapPut("/{userId:guid}", async (Guid restaurantId, Guid userId, HttpContext context, ManageIdentityAccess command, CancellationToken ct) =>
        { await command.MembershipAsync(context.User.ToActor(), restaurantId, userId, true, ct); return Results.NoContent(); });
        memberships.MapDelete("/{userId:guid}", async (Guid restaurantId, Guid userId, HttpContext context, ManageIdentityAccess command, CancellationToken ct) =>
        { await command.MembershipAsync(context.User.ToActor(), restaurantId, userId, false, ct); return Results.NoContent(); });
    }
}
