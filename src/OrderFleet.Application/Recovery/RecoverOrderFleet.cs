using OrderFleet.Application.Clock;
using OrderFleet.Application.Dispatch;
using OrderFleet.Application.Payments;
using OrderFleet.Application.Diagnostics;
using OrderFleet.Domain.Orders;

namespace OrderFleet.Application.Recovery;

public sealed class RecoverOrderFleet(IRecoveryStore store, DispatchOrder dispatch, IPaymentProvider provider,
    IClock clock, RecoveryPolicy policy)
{
    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        using var activity = OrderFleetDiagnostics.Activities.StartActivity("recovery.sweep");
        var now = clock.UtcNow;
        var examined = 0;
        await store.ResolveObsoleteFailuresAsync(now, policy.BatchSize, ct);
        foreach (var orderId in await store.ExpiredOrdersAsync(now, policy.BatchSize, ct))
        {
            examined++;
            await PerformAsync(RecoveryWork.AcceptanceTimeout, orderId, async () => { await store.ExpireOrderAsync(orderId, now, ct); }, ct);
        }
        foreach (var assignment in await store.ExpiredAssignmentsAsync(now, policy.BatchSize, ct))
        {
            examined++;
            await PerformAsync(RecoveryWork.AssignmentTimeout, assignment.AssignmentId,
                async () => { await store.ReleaseExpiredAssignmentAsync(assignment.OrderId, assignment.AssignmentId, now, ct); }, ct);
        }
        foreach (var orderId in await store.DueDispatchAsync(now, policy.BatchSize, ct))
        {
            examined++;
            await PerformAsync(RecoveryWork.DispatchRetry, orderId, async () => { await dispatch.ExecuteAsync(orderId, null, ct); }, ct);
        }
        foreach (var payment in await store.UnsettledCancellationsAsync(now, policy.BatchSize, ct))
        {
            examined++;
            await PerformAsync(RecoveryWork.PaymentSettlement, payment.OrderId, () => store.ReconcileCancellationAsync(payment, ct), ct);
        }
        foreach (var orderId in await store.UncertainPaymentsAsync(now, policy.BatchSize, ct))
        {
            examined++;
            await PerformAsync(RecoveryWork.CaptureReconciliation, orderId, () => store.ReconcileCaptureAsync(orderId, ct), ct);
        }
        foreach (var authorization in await store.OrphanAuthorizationsAsync(now - policy.OrphanGrace, now, policy.BatchSize, ct))
        {
            examined++;
            await PerformAsync(RecoveryWork.OrphanAuthorization, authorization, () => provider.ReleaseUnusedAuthorizationAsync(authorization, ct), ct);
        }
        return examined;
    }

    private async Task PerformAsync(string operation, Guid id, Func<Task> action, CancellationToken ct)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var permanent = error is DomainException or ArgumentException or InvalidDataException;
            OrderFleetDiagnostics.Errors.Add(1, new System.Diagnostics.TagList { { "component", "recovery" }, { "operation", operation } });
            await store.RecordFailureAsync(operation, id, clock.UtcNow, permanent ? "InvariantViolation" : "DependencyOrConflict",
                permanent ? 1 : policy.MaxFailureAttempts, ct);
            return;
        }
        await store.RecordResolvedAsync(operation, id, clock.UtcNow, ct);
    }
}
