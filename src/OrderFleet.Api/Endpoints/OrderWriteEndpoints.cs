using System.Security.Claims;
using OrderFleet.Api.Identity;
using OrderFleet.Application.Identity;
using OrderFleet.Application.Orders;

namespace OrderFleet.Api.Endpoints;

public static class OrderWriteEndpoints
{
    public static void MapOrderWriteEndpoints(this WebApplication app)
    {
        var orders = app.MapGroup("/api/v1/orders");
        orders.MapPost("/", async (CreateOrderRequest request, HttpRequest http, ClaimsPrincipal principal,
            CreateOrder useCase, CancellationToken token) =>
        {
            var keys = http.Headers["Idempotency-Key"];
            var id = await useCase.ExecuteAsync(principal.ToActor(), request, keys.Count == 1 ? keys[0] : null, token);
            return Results.Created($"/api/v1/orders/{id}", new { id });
        }).RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.Customer));
        orders.MapPost("/{id:guid}/accept", async (Guid id, ClaimsPrincipal principal, OrderCommands useCase, CancellationToken token) =>
        {
            await useCase.AcceptAsync(id, principal.ToActor(), token);
            return Results.NoContent();
        }).RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.RestaurantOwner, OrderFleetRoles.RestaurantStaff));
        orders.MapPost("/{id:guid}/reject", async (Guid id, ClaimsPrincipal principal, OrderCommands useCase, CancellationToken token) =>
        {
            await useCase.RejectAsync(id, principal.ToActor(), token);
            return Results.NoContent();
        }).RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.RestaurantOwner, OrderFleetRoles.RestaurantStaff));
        orders.MapPost("/{id:guid}/cancel", async (Guid id, ClaimsPrincipal principal, OrderCommands useCase,
            ILoggerFactory loggers, CancellationToken token) =>
        {
            var actor = principal.ToActor();
            await useCase.CancelAsync(id, actor, token);
            if (actor.IsInRole(OrderFleetRoles.Admin))
                loggers.CreateLogger("OrderAccessAudit").LogInformation("Admin {ActorId} cancelled order {OrderId}", actor.Id, id);
            return Results.NoContent();
        }).RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.Customer, OrderFleetRoles.RestaurantOwner,
            OrderFleetRoles.RestaurantStaff, OrderFleetRoles.Admin));
        orders.MapGet("/", async (Guid? restaurantId, int? page, int? pageSize, ClaimsPrincipal principal,
            QueryOrders query, CancellationToken token) => Results.Ok(await query.ListAsync(principal.ToActor(), restaurantId,
                page ?? 1, pageSize ?? 20, token))).RequireAuthorization();
        orders.MapGet("/{id:guid}/delivery", async (Guid id, ClaimsPrincipal principal, QueryOrders query,
            ILoggerFactory loggers, CancellationToken token) =>
        {
            var actor = principal.ToActor();
            var details = await query.DeliveryAsync(id, actor, token);
            if (actor.IsInRole(OrderFleetRoles.Admin))
                loggers.CreateLogger("OrderAccessAudit").LogInformation("Admin {ActorId} read delivery for order {OrderId}", actor.Id, id);
            return Results.Ok(details);
        }).RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.All));
    }
}
