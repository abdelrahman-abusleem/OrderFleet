using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderFleet.Application.Messaging;
using OrderFleet.Application.Payments;
using OrderFleet.Domain.Orders;
using OrderFleet.Domain.Payments;
using OrderFleet.Infrastructure.Payments;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class CaptureCancellationRaceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(false, PaymentState.Voided, "payment.voided.v1", "Void")]
    [InlineData(true, PaymentState.Refunded, "payment.refunded.v1", "Refund")]
    public async Task CaptureAndCancellation_BothInterleavingsRetainObligationAndConverge(bool captureFirst,
        PaymentState terminalPayment, string financialType, string providerOperation)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var factory = new ApiFactory(fixture);
        factory.CustomizeServices = services =>
        {
            services.RemoveAll<IPaymentProvider>();
            services.AddScoped<IPaymentProvider>(sp => new PausedCapture(sp.GetRequiredService<DurableMockPaymentProvider>(),
                captureFirst, entered, resume));
        };
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        using var customer = await ClientAsync(factory, "Customer");
        var (restaurant, product) = await CatalogAsync(owner);
        var created = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(timeout.Token)).GetProperty("id").GetGuid();
        var accepting = owner.PostAsync($"/api/v1/orders/{id}/accept", null, timeout.Token);
        Guid cancellationMessage;
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            Assert.Equal(HttpStatusCode.NoContent, (await customer.PostAsync($"/api/v1/orders/{id}/cancel", null, timeout.Token)).StatusCode);
            await using var snapshot = fixture.CreateContext();
            Assert.Equal(OrderStatus.Cancelled, (await snapshot.Orders.SingleAsync(x => x.Id == id, timeout.Token)).Status);
            var obligation = await snapshot.OutboxMessages.SingleAsync(x => x.AggregateId == id && x.Type == "order.cancelled.v1", timeout.Token);
            cancellationMessage = obligation.Id;

            Assert.Null(obligation.PublishedAtUtc);
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SettleCancelledPayment>().ExecuteAsync(cancellationMessage, id, timeout.Token);
        }
        finally { resume.TrySetResult(); }
        Assert.Equal(HttpStatusCode.Conflict, (await accepting).StatusCode);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SettleCancelledPayment>().ExecuteAsync(cancellationMessage, id, timeout.Token);

        await using (var persisted = fixture.CreateContext())
        {
            var facts = await persisted.OutboxMessages.AsNoTracking().Where(x => x.AggregateId == id && x.Type.StartsWith("payment.")).ToArrayAsync(timeout.Token);
            foreach (var fact in facts)
            {
                using var scope = factory.Services.CreateScope();
                var envelope = new IntegrationEvent(fact.Id, fact.Type, fact.Version, fact.OccurredAtUtc,
                    fact.AggregateId, fact.CorrelationId, fact.PayloadJson);
                await scope.ServiceProvider.GetRequiredService<ArchiveFinancialEvent>().HandleAsync(envelope, timeout.Token);
                await scope.ServiceProvider.GetRequiredService<ArchiveFinancialEvent>().HandleAsync(envelope, timeout.Token);
            }
        }
        await using var verify = fixture.CreateContext();
        var order = await verify.Orders.SingleAsync(x => x.Id == id, timeout.Token);
        var payment = await verify.Payments.SingleAsync(x => x.OrderId == id, timeout.Token);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(terminalPayment, payment.State);
        Assert.Null(order.AssignmentId);
        Assert.False(await verify.OutboxMessages.AnyAsync(x => x.AggregateId == id && x.Type == "order.accepted.v1", timeout.Token));
        Assert.Equal(1, await verify.OutboxMessages.CountAsync(x => x.AggregateId == id && x.Type == financialType, timeout.Token));
        Assert.Equal(1, await verify.MockPaymentEffects.CountAsync(x => x.AuthorizationId == payment.AuthorizationId && x.Operation == providerOperation && x.Succeeded, timeout.Token));
        Assert.Equal(captureFirst ? 1 : 0, await verify.MockPaymentEffects.CountAsync(x => x.AuthorizationId == payment.AuthorizationId && x.Operation == "Capture" && x.Succeeded, timeout.Token));
        Assert.Equal(captureFirst ? 3 : 2, await verify.FinancialAuditRecords.CountAsync(x => x.OrderId == id, timeout.Token));
        var history = await verify.OutboxMessages.Where(x => x.AggregateId == id && x.Type.StartsWith("payment."))
            .OrderBy(x => x.Sequence).Select(x => x.Type).ToArrayAsync(timeout.Token);
        Assert.Equal(captureFirst
            ? ["payment.authorized.v1", "payment.captured.v1", "payment.refunded.v1"]
            : new[] { "payment.authorized.v1", "payment.voided.v1" }, history);
        Assert.Equal(captureFirst ? 1 : 0, await verify.PaymentCaptureAttempts.CountAsync(x => x.PaymentId == payment.Id
            && x.Classification == CaptureClassification.Succeeded, timeout.Token));
        if (captureFirst)
        {
            var capture = await verify.FinancialAuditRecords.SingleAsync(x => x.OrderId == id && x.Type == "payment.captured.v1", timeout.Token);
            Assert.Equal("Captured", capture.State);
            Assert.Equal(payment.CapturedAt, capture.OccurredAtUtc);
            Assert.True(capture.OccurredAtUtc <= payment.SettledAt);
        }
        var settledFact = await verify.FinancialAuditRecords.SingleAsync(x => x.OrderId == id && x.Type == financialType, timeout.Token);
        Assert.Equal(payment.Id, settledFact.PaymentId);
        Assert.Equal(payment.Amount, settledFact.Amount);
        Assert.Equal(terminalPayment.ToString(), settledFact.State);
        Assert.Equal(1, await verify.InboxMessages.CountAsync(x => x.MessageId == cancellationMessage && x.Consumer == "payments.cancelled.v1", timeout.Token));
    }

    private sealed class PausedCapture(IPaymentProvider inner, bool captureFirst, TaskCompletionSource entered, TaskCompletionSource resume) : IPaymentProvider
    {
        public Task<ProviderAuthorization> AuthorizeAsync(Guid actorId, string key, string fingerprint, decimal amount, string currency, CancellationToken ct) =>
            inner.AuthorizeAsync(actorId, key, fingerprint, amount, currency, ct);
        public async Task<ProviderCapture> CaptureAsync(Guid authorizationId, CancellationToken ct)
        {
            var result = captureFirst ? await inner.CaptureAsync(authorizationId, ct) : null;
            entered.TrySetResult();
            await resume.Task.WaitAsync(ct);
            return result ?? await inner.CaptureAsync(authorizationId, ct);
        }
        public Task<ProviderCapture?> FindCaptureAsync(Guid authorizationId, CancellationToken ct) => inner.FindCaptureAsync(authorizationId, ct);
        public Task<ProviderSettlement> SettleAsync(Guid authorizationId, CancellationToken ct) => inner.SettleAsync(authorizationId, ct);
        public Task ReleaseUnusedAuthorizationAsync(Guid authorizationId, CancellationToken ct) => inner.ReleaseUnusedAuthorizationAsync(authorizationId, ct);
    }
}
