using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Recovery;
using OrderFleet.Domain.Orders;
using OrderFleet.Infrastructure.Persistence;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class RecoveryFailureIsolationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedItem_IsBoundedAndVisible_OtherOrdersRecover_AndAuditedRetryCanResume(bool permanent)
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var failedOrder = Guid.Empty;
        var inject = true;
        await using var factory = new ApiFactory(fixture, clock);
        factory.Overrides["Recovery:MaxFailureAttempts"] = "2";
        factory.CustomizeServices = services =>
        {
            services.RemoveAll<IRecoveryStore>();
            services.AddScoped<IRecoveryStore>(sp => new ExpiryFailure(sp.GetRequiredService<EfRecoveryStore>(), failedOrder, inject, permanent));
        };
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        using var admin = await ClientAsync(factory, "Admin");
        async Task<Guid> CreateOrderAsync()
        {
            var created = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }
        failedOrder = await CreateOrderAsync();
        var independentOrder = await CreateOrderAsync();
        clock.Advance(TimeSpan.FromMinutes(5));
        await RecoveryTests.RunAsync(factory);
        await using (var context = fixture.CreateContext())
        {
            Assert.Equal(OrderStatus.Created, (await context.Orders.SingleAsync(x => x.Id == failedOrder)).Status);
            Assert.Equal(OrderStatus.Cancelled, (await context.Orders.SingleAsync(x => x.Id == independentOrder)).Status);
            var failure = await context.RecoveryWorkFailures.SingleAsync(x => x.ResourceId == failedOrder);
            Assert.Equal(1, failure.Attempts);
            Assert.Equal(permanent, failure.DeadLetteredAtUtc.HasValue);
            Assert.DoesNotContain("private-marker", failure.LastErrorCode!);
        }
        await RecoveryTests.RunAsync(factory);
        if (!permanent)
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            await RecoveryTests.RunAsync(factory);
        }
        var failures = await admin.GetFromJsonAsync<JsonElement>("/api/v1/operations/recovery-failures?state=dead");
        Assert.Contains(failures.EnumerateArray(), item => item.GetProperty("resourceId").GetGuid() == failedOrder);
        inject = false;
        await RecoveryTests.RunAsync(factory);
        await using (var context = fixture.CreateContext())
            Assert.Equal(OrderStatus.Created, (await context.Orders.SingleAsync(x => x.Id == failedOrder)).Status);
        var key = Guid.NewGuid().ToString("N");
        using (var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/operations/recovery/{RecoveryWork.AcceptanceTimeout}/{failedOrder}/retry"))
        {
            request.Headers.Add("Idempotency-Key", key);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(request)).StatusCode);
        }
        await RecoveryTests.RunAsync(factory);
        await using var verify = fixture.CreateContext();
        Assert.Equal(CancellationReason.AcceptanceTimedOut, (await verify.Orders.SingleAsync(x => x.Id == failedOrder)).CancelReason);
        Assert.NotNull((await verify.RecoveryWorkFailures.SingleAsync(x => x.ResourceId == failedOrder)).ResolvedAtUtc);
        var audit = await verify.OperationAudits.SingleAsync(x => x.TargetId == failedOrder && x.Action == "RetryRecovery:AcceptanceTimeout");
        Assert.Equal(permanent ? 1 : 2, audit.PreviousAttempts);
        Assert.Equal(permanent ? "InvariantViolation" : "DependencyOrConflict", audit.PreviousOutcome);
    }



    private sealed class ExpiryFailure(IRecoveryStore inner, Guid failedOrder, bool inject, bool permanent) : IRecoveryStore
    {
        public Task<IReadOnlyList<Guid>> ExpiredOrdersAsync(DateTimeOffset now, int count, CancellationToken ct) => inner.ExpiredOrdersAsync(now, count, ct);
        public Task<bool> ExpireOrderAsync(Guid id, DateTimeOffset now, CancellationToken ct)
        {
            if (inject && id == failedOrder)
            {
                if (permanent) throw new InvalidDataException("injected-private-marker");
                throw new DependencyUnavailableException();
            }
            return inner.ExpireOrderAsync(id, now, ct);
        }
        public Task<IReadOnlyList<ExpiredAssignment>> ExpiredAssignmentsAsync(DateTimeOffset now, int count, CancellationToken ct) => inner.ExpiredAssignmentsAsync(now, count, ct);
        public Task<bool> ReleaseExpiredAssignmentAsync(Guid id, Guid fence, DateTimeOffset now, CancellationToken ct) => inner.ReleaseExpiredAssignmentAsync(id, fence, now, ct);
        public Task<IReadOnlyList<Guid>> DueDispatchAsync(DateTimeOffset now, int count, CancellationToken ct) => inner.DueDispatchAsync(now, count, ct);
        public Task<IReadOnlyList<CancelledPayment>> UnsettledCancellationsAsync(DateTimeOffset now, int count, CancellationToken ct) => inner.UnsettledCancellationsAsync(now, count, ct);
        public Task ReconcileCancellationAsync(CancelledPayment payment, CancellationToken ct) => inner.ReconcileCancellationAsync(payment, ct);
        public Task<IReadOnlyList<Guid>> UncertainPaymentsAsync(DateTimeOffset now, int count, CancellationToken ct) => inner.UncertainPaymentsAsync(now, count, ct);
        public Task ReconcileCaptureAsync(Guid id, CancellationToken ct) => inner.ReconcileCaptureAsync(id, ct);
        public Task<IReadOnlyList<Guid>> OrphanAuthorizationsAsync(DateTimeOffset cutoff, DateTimeOffset now, int count, CancellationToken ct) => inner.OrphanAuthorizationsAsync(cutoff, now, count, ct);
        public Task RecordFailureAsync(string operation, Guid id, DateTimeOffset now, string code, int maximum, CancellationToken ct) => inner.RecordFailureAsync(operation, id, now, code, maximum, ct);
        public Task RecordResolvedAsync(string operation, Guid id, DateTimeOffset now, CancellationToken ct) => inner.RecordResolvedAsync(operation, id, now, ct);
        public Task ResolveObsoleteFailuresAsync(DateTimeOffset now, int count, CancellationToken ct) => inner.ResolveObsoleteFailuresAsync(now, count, ct);
    }
}
