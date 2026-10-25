using OrderFleet.Application.Clock;
using OrderFleet.Application.Dispatch;
using OrderFleet.Application.Payments;

namespace OrderFleet.Application.Recovery;

public sealed class RecoverOrderFleet(IRecoveryStore store, DispatchOrder dispatch, IPaymentProvider provider,
    IClock clock, RecoveryPolicy policy)
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        foreach (var orderId in await store.ExpiredOrdersAsync(now, policy.BatchSize, ct))
            await store.ExpireOrderAsync(orderId, now, ct);
        foreach (var assignment in await store.ExpiredAssignmentsAsync(now, policy.BatchSize, ct))
            await store.ReleaseExpiredAssignmentAsync(assignment.OrderId, assignment.AssignmentId, now, ct);
        foreach (var orderId in await store.DueDispatchAsync(now, policy.BatchSize, ct))
            await dispatch.ExecuteAsync(orderId, null, ct);
        foreach (var payment in await store.UnsettledCancellationsAsync(policy.BatchSize, ct))
            await store.ReconcileCancellationAsync(payment, ct);
        foreach (var orderId in await store.UncertainPaymentsAsync(policy.BatchSize, ct))
            await store.ReconcileCaptureAsync(orderId, ct);
        foreach (var authorization in await store.OrphanAuthorizationsAsync(now - policy.OrphanGrace, policy.BatchSize, ct))
            await provider.ReleaseUnusedAuthorizationAsync(authorization, ct);
    }
}
