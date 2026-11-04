using System.Security.Claims;
using OrderFleet.Api.Identity;
using OrderFleet.Application.Eta;
using OrderFleet.Application.Identity;

namespace OrderFleet.Api.Endpoints;

public static class EtaEndpoints
{
    public static void MapEtaEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/orders/{id:guid}/eta", async (Guid id, ClaimsPrincipal principal, GetEta query, CancellationToken ct) =>
            Results.Ok(await query.ExecuteAsync(id, principal.ToActor(), ct))).RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.All));
    }
}
