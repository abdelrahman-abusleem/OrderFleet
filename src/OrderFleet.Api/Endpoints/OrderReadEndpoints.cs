using System.Security.Claims;
using OrderFleet.Application.Identity;
using OrderFleet.Application.Orders;

namespace OrderFleet.Api.Endpoints;

public static class OrderReadEndpoints
{
    public static void MapOrderReadEndpoints(this WebApplication app) =>
        app.MapGet("/api/v1/orders/{id:guid}", ReadAsync)
            .RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.All));

    private static async Task<IResult> ReadAsync(Guid id, ClaimsPrincipal principal, GetOrder useCase,
        ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var actor = new Actor(Guid.Parse(principal.FindFirst("sub")!.Value),
            principal.FindAll("role").Select(x => x.Value).ToHashSet(StringComparer.Ordinal));
        var order = await useCase.ExecuteAsync(id, actor, cancellationToken);
        if (actor.IsInRole(OrderFleetRoles.Admin))
            loggers.CreateLogger("OrderAccessAudit").LogInformation("Admin {ActorId} read order {OrderId}", actor.Id, id);
        return order is null ? Results.NotFound() : Results.Ok(order);
    }
}
