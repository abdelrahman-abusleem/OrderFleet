using OrderFleet.Application.Catalog;
using OrderFleet.Application.Clock;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Identity;
using OrderFleet.Application.Recovery;

namespace OrderFleet.Application.Operations;

public sealed class OperateOrderFleet(IOperationsStore store, IClock clock)
{
    public async Task<OperationsSummary> SummaryAsync(Actor actor, CancellationToken ct)
    { await RequireAdminAsync(actor, ct); return await store.SummaryAsync(clock.UtcNow, ct); }

    public async Task<IReadOnlyList<OutboxDiagnostic>> OutboxAsync(Actor actor, string state, int page, int size, CancellationToken ct)
    {
        await RequireAdminAsync(actor, ct);
        if (state is not ("pending" or "dead")) throw new RequestValidationException("State must be pending or dead.");
        return await store.OutboxAsync(state, BrowseCatalog.Offset(page, size), size, ct);
    }

    public async Task RetryOutboxAsync(Actor actor, Guid id, string? key, CancellationToken ct)
    { await RequireAdminAsync(actor, ct); ValidateKey(key); await store.RetryOutboxAsync(id, actor.Id, key!, clock.UtcNow, ct); }
    public async Task RetryDispatchAsync(Actor actor, Guid id, string? key, CancellationToken ct)
    { await RequireAdminAsync(actor, ct); ValidateKey(key); await store.RetryDispatchAsync(id, actor.Id, key!, clock.UtcNow, ct); }

    public async Task<IReadOnlyList<RecoveryDiagnostic>> RecoveryFailuresAsync(Actor actor, string state, int page, int size, CancellationToken ct)
    {
        await RequireAdminAsync(actor, ct);
        if (state is not ("dead" or "pending")) throw new RequestValidationException("State must be pending or dead.");
        return await store.RecoveryFailuresAsync(state, BrowseCatalog.Offset(page, size), size, ct);
    }
    public async Task RetryRecoveryAsync(Actor actor, string operation, Guid id, string? key, CancellationToken ct)
    {
        await RequireAdminAsync(actor, ct);
        ValidateKey(key);
        if (!RecoveryWork.All.Contains(operation)) throw new RequestValidationException("Unknown recovery operation.");
        await store.RetryRecoveryAsync(operation, id, actor.Id, key!, clock.UtcNow, ct);
    }

    private async Task RequireAdminAsync(Actor actor, CancellationToken ct)
    { if (!actor.IsInRole(OrderFleetRoles.Admin) || !await store.IsAdministratorAsync(actor.Id, ct)) throw new AccessDeniedException(); }
    private static void ValidateKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100 || key.Any(char.IsControl))
            throw new RequestValidationException("An Idempotency-Key of at most100 characters is required.");
    }
}
