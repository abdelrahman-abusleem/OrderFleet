using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using OrderFleet.Application.Clock;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Payments;
using OrderFleet.Infrastructure.Persistence;

namespace OrderFleet.Infrastructure.Payments;

// An explicit mock provider ledger in separate transactions simulates durable external
// financial effects. No real money, cards, gateway credentials or network charges.
public sealed class DurableMockPaymentProvider(IDbContextFactory<OrderFleetDbContext> contexts,
    IOptions<MockPaymentOptions> options, IClock clock) : IPaymentProvider
{
    public async Task<ProviderAuthorization> AuthorizeAsync(Guid actorId, string key, string fingerprint,
        decimal amount, string currency, CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty || string.IsNullOrWhiteSpace(key) || key.Length > 100 || fingerprint.Length != 64)
            throw new ArgumentException("Invalid authorization scope.");
        if (amount <= 0 || decimal.Round(amount, 2) != amount || currency != "SAR")
            throw new ArgumentException("Invalid authorization amount or currency.");
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var existing = await context.MockPaymentAuthorizations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ActorId == actorId && x.Key == key, cancellationToken);
        if (existing is not null) return Replay(existing, fingerprint, amount, currency);
        if (options.Value.AuthorizeOutcome == "Transient") throw new DependencyUnavailableException();
        var now = clock.UtcNow;
        var authorization = new MockPaymentAuthorization(actorId, key, fingerprint, amount, currency,
            options.Value.AuthorizeOutcome == "Success", now);
        context.MockPaymentAuthorizations.Add(authorization);
        context.MockPaymentEffects.Add(new(authorization.Id, "Authorize", authorization.State == ProviderPaymentState.Authorized, now));
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_MockPaymentAuthorizations_ActorId_Key" })
        {
            await using var fresh = await contexts.CreateDbContextAsync(cancellationToken);
            var winner = await fresh.MockPaymentAuthorizations.AsNoTracking()
                .SingleAsync(x => x.ActorId == actorId && x.Key == key, cancellationToken);
            return Replay(winner, fingerprint, amount, currency);
        }
        return Describe(authorization);
    }

    public async Task<ProviderCapture> CaptureAsync(Guid authorizationId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var authorization = await LockAsync(context, authorizationId, cancellationToken);
        var previous = await context.MockPaymentEffects.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AuthorizationId == authorizationId && x.Operation == "Capture", cancellationToken);
        if (previous is not null) return new(previous.Succeeded, previous.OccurredAt);
        if (authorization.State != ProviderPaymentState.Authorized) return new(false, clock.UtcNow);
        if (options.Value.CaptureOutcome == "Transient") throw new DependencyUnavailableException();
        var unknown = options.Value.CaptureOutcome == "Unknown";
        var success = options.Value.CaptureOutcome == "Success" || unknown;
        var now = clock.UtcNow;
        if (success) authorization.Capture(now);
        context.MockPaymentEffects.Add(new(authorizationId, "Capture", success, now));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (unknown) throw new ProviderOutcomeUnknownException();
        return new(success, now);
    }

    public async Task<ProviderSettlement> SettleAsync(Guid authorizationId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var authorization = await LockAsync(context, authorizationId, cancellationToken);
        if (authorization.State is ProviderPaymentState.Voided or ProviderPaymentState.Refunded)
            return new(authorization.State, authorization.SettledAt!.Value, authorization.CapturedAt);
        if (authorization.State == ProviderPaymentState.Declined) throw new PaymentDeclinedException();
        authorization.Settle(clock.UtcNow);
        context.MockPaymentEffects.Add(new(authorizationId,
            authorization.State == ProviderPaymentState.Refunded ? "Refund" : "Void", true, authorization.SettledAt!.Value));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(authorization.State, authorization.SettledAt!.Value, authorization.CapturedAt);
    }

    public async Task<ProviderCapture?> FindCaptureAsync(Guid authorizationId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var effect = await context.MockPaymentEffects.AsNoTracking().SingleOrDefaultAsync(
            x => x.AuthorizationId == authorizationId && x.Operation == "Capture", cancellationToken);
        return effect is null ? null : new(effect.Succeeded, effect.OccurredAt);
    }

    public async Task ReleaseUnusedAuthorizationAsync(Guid authorizationId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var authorization = await LockAsync(context, authorizationId, cancellationToken);
        // Creation takes a shared authorization lock before attaching a Payment. This
        // check cannot void another concurrent request's committed order authorization.
        if (authorization.State != ProviderPaymentState.Authorized
            || await context.Payments.AnyAsync(x => x.AuthorizationId == authorizationId, cancellationToken)) return;
        authorization.Settle(clock.UtcNow);
        context.MockPaymentEffects.Add(new(authorizationId, "Void", true, authorization.SettledAt!.Value));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static Task<MockPaymentAuthorization> LockAsync(OrderFleetDbContext context, Guid id, CancellationToken cancellationToken) =>
        context.MockPaymentAuthorizations.FromSqlInterpolated(
            $"SELECT *, xmin FROM \"MockPaymentAuthorizations\" WHERE \"Id\" = {id} FOR UPDATE")
            .SingleAsync(cancellationToken);

    private static ProviderAuthorization Replay(MockPaymentAuthorization authorization, string fingerprint, decimal amount, string currency)
    {
        if (authorization.Fingerprint != fingerprint || authorization.Amount != amount || authorization.Currency != currency)
            throw new OperationConflictException("Payment request key does not match the original authorization.");
        return Describe(authorization);
    }
    private static ProviderAuthorization Describe(MockPaymentAuthorization authorization) =>
        new(authorization.Id, authorization.State, authorization.Amount, authorization.Currency, authorization.CreatedAt);
}
