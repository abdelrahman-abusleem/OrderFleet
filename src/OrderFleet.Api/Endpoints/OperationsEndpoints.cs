using System.Security.Claims;
using OrderFleet.Api.Identity;
using OrderFleet.Application.Identity;
using OrderFleet.Application.Operations;
using OrderFleet.Application.Errors;

namespace OrderFleet.Api.Endpoints;

public static class OperationsEndpoints
{
    public static void MapOperationsEndpoints(this WebApplication app)
    {
        var operations = app.MapGroup("/api/v1/operations").RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.Admin));
        operations.MapGet("/summary", async (ClaimsPrincipal principal, OperateOrderFleet query, CancellationToken ct) =>
            Results.Ok(await query.SummaryAsync(principal.ToActor(), ct)));
        operations.MapGet("/outbox", async (string? state, int? page, int? pageSize, ClaimsPrincipal principal,
            OperateOrderFleet query, CancellationToken ct) =>
            Results.Ok(await query.OutboxAsync(principal.ToActor(), state ?? "dead", page ?? 1, pageSize ?? 20, ct)));
        operations.MapPost("/outbox/{id:guid}/retry", async (Guid id, HttpContext context, OperateOrderFleet command, CancellationToken ct) =>
        {
            await command.RetryOutboxAsync(context.User.ToActor(), id, RequestKey(context), ct);
            return Results.NoContent();
        });
        operations.MapPost("/orders/{id:guid}/retry-dispatch", async (Guid id, HttpContext context, OperateOrderFleet command, CancellationToken ct) =>
        {
            await command.RetryDispatchAsync(context.User.ToActor(), id, RequestKey(context), ct);
            return Results.NoContent();
        });
        operations.MapGet("/recovery-failures", async (string? state, int? page, int? pageSize, ClaimsPrincipal principal,
            OperateOrderFleet query, CancellationToken ct) =>
            Results.Ok(await query.RecoveryFailuresAsync(principal.ToActor(), state ?? "dead", page ?? 1, pageSize ?? 20, ct)));
        operations.MapPost("/recovery/{operation}/{id:guid}/retry", async (string operation, Guid id, HttpContext context,
            OperateOrderFleet command, CancellationToken ct) =>
        {
            await command.RetryRecoveryAsync(context.User.ToActor(), operation, id, RequestKey(context), ct);
            return Results.NoContent();
        });
    }
    private static string? RequestKey(HttpContext context)
    {
        var values = context.Request.Headers["Idempotency-Key"];
        if (values.Count > 1) throw new RequestValidationException("One Idempotency-Key is required.");
        return values.Count == 1 ? values[0] : null;
    }
}
