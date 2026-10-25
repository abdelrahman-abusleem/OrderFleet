using System.Security.Claims;
using OrderFleet.Api.Identity;
using OrderFleet.Application.Dispatch;
using OrderFleet.Application.Identity;

namespace OrderFleet.Api.Endpoints;

public static class DispatchEndpoints
{
    public sealed record AssignmentRequest(Guid AssignmentId);

    public static void MapDispatchEndpoints(this WebApplication app)
    {
        var orders = app.MapGroup("/api/v1/orders");
        orders.MapPost("/{id:guid}/pickup", async (Guid id, AssignmentRequest request, ClaimsPrincipal principal,
            CourierOrderCommands command, CancellationToken ct) =>
        {
            await command.ExecuteAsync(id, principal.ToActor(), request.AssignmentId, CourierOrderAction.Pickup, ct);
            return Results.NoContent();
        }).RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.Courier));
        orders.MapPost("/{id:guid}/deliver", async (Guid id, AssignmentRequest request, ClaimsPrincipal principal,
            CourierOrderCommands command, CancellationToken ct) =>
        {
            await command.ExecuteAsync(id, principal.ToActor(), request.AssignmentId, CourierOrderAction.Deliver, ct);
            return Results.NoContent();
        }).RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.Courier));
        orders.MapPost("/{id:guid}/release-assignment", async (Guid id, AssignmentRequest request, ClaimsPrincipal principal,
            CourierOrderCommands command, CancellationToken ct) =>
        {
            await command.ExecuteAsync(id, principal.ToActor(), request.AssignmentId, CourierOrderAction.Release, ct);
            return Results.NoContent();
        }).RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.Courier, OrderFleetRoles.Admin));
    }
}
