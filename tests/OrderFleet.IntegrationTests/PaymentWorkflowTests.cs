using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderFleet.Infrastructure.Payments;
using OrderFleet.Application.Payments;
using OrderFleet.Domain.Orders;
using OrderFleet.Domain.Payments;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class PaymentWorkflowTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("Declined", PaymentState.Failed, CaptureClassification.PermanentDecline, HttpStatusCode.Conflict)]
    [InlineData("Transient", PaymentState.Failed, CaptureClassification.TransientFailure, HttpStatusCode.ServiceUnavailable)]
    [InlineData("Unknown", PaymentState.PendingReconciliation, CaptureClassification.Unknown, HttpStatusCode.ServiceUnavailable)]
    public async Task CaptureFailure_PersistsClassification_AndRestartReplaysOrRetriesSafely(string outcome,
        PaymentState failedState, CaptureClassification classification, HttpStatusCode expected)
    {
        await using var factory = new ApiFactory(fixture);
        factory.Overrides["MockPayments:CaptureOutcome"] = outcome;
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        var created = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(expected, (await owner.PostAsync($"/api/v1/orders/{id}/accept", null)).StatusCode);
        await using (var context = fixture.CreateContext())
        {
            var payment = await context.Payments.SingleAsync(x => x.OrderId == id);
            Assert.Equal(failedState, payment.State);
            Assert.Equal(OrderStatus.Created, (await context.Orders.SingleAsync(x => x.Id == id)).Status);
            var attempt = await context.PaymentCaptureAttempts.SingleAsync(x => x.PaymentId == payment.Id);
            Assert.Equal(classification, attempt.Classification);
            Assert.Equal(outcome != "Declined", attempt.IsRetryable);
            Assert.Equal(outcome == "Unknown", attempt.RequiresReconciliation);
        }
        // A new host shares only persisted provider state and the token signing key.
        await using var restarted = new ApiFactory(fixture, signingKey: factory.SigningKey);
        using var retry = restarted.CreateClient(new() { BaseAddress = new("https://localhost") });
        retry.DefaultRequestHeaders.Authorization = owner.DefaultRequestHeaders.Authorization;
        Assert.Equal(outcome == "Declined" ? HttpStatusCode.Conflict : HttpStatusCode.NoContent,
            (await retry.PostAsync($"/api/v1/orders/{id}/accept", null)).StatusCode);
        await using var verify = fixture.CreateContext();
        var stored = await verify.Payments.SingleAsync(x => x.OrderId == id);
        Assert.Equal(outcome == "Declined" ? PaymentState.Failed : PaymentState.Captured, stored.State);
        var effects = await verify.MockPaymentEffects.Where(x => x.AuthorizationId == stored.AuthorizationId && x.Operation == "Capture").ToArrayAsync();
        Assert.Single(effects);
        Assert.Equal(outcome != "Declined", effects[0].Succeeded);
        var attempts = await verify.PaymentCaptureAttempts.Where(x => x.PaymentId == stored.Id).ToArrayAsync();
        Assert.Equal(2, attempts.Length);
        Assert.Single(attempts.Select(x => x.IdempotencyKey).Distinct());
    }

    [Fact]
    public async Task CaptureFinishingAtDeadline_PersistsFinancialFactWithoutAccepting_ThenRefunds()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var factory = new ApiFactory(fixture, clock);
        factory.CustomizeServices = services =>
        {
            services.RemoveAll<IPaymentProvider>();
            services.AddScoped<IPaymentProvider>(sp => new AdvancingCaptureProvider(
                sp.GetRequiredService<DurableMockPaymentProvider>(), clock));
        };
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        using var admin = await ClientAsync(factory, "Admin");
        var created = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/v1/orders/{id}/accept", null)).StatusCode);
        await using (var context = fixture.CreateContext())
        {
            Assert.Equal(OrderStatus.Created, (await context.Orders.SingleAsync(x => x.Id == id)).Status);
            Assert.Equal(PaymentState.Captured, (await context.Payments.SingleAsync(x => x.OrderId == id)).State);
            Assert.False(await context.OutboxMessages.AnyAsync(x => x.AggregateId == id && x.Type == "order.accepted.v1"));
        }
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/orders/{id}/cancel", null)).StatusCode);
        await SettleAsync(factory, id);
        await using var verify = fixture.CreateContext();
        Assert.Equal(PaymentState.Refunded, (await verify.Payments.SingleAsync(x => x.OrderId == id)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureCommittedBeforeLocalSave_CancellationReconcilesAndRefunds(bool lostResponse)
    {
        await using var factory = new ApiFactory(fixture);
        if (lostResponse) factory.Overrides["MockPayments:CaptureOutcome"] = "Unknown";
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        var created = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        if (lostResponse)
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await owner.PostAsync($"/api/v1/orders/{id}/accept", null)).StatusCode);
        else
        {
            await using var context = fixture.CreateContext();
            var authorization = (await context.Payments.SingleAsync(x => x.OrderId == id)).AuthorizationId;
            using var scope = factory.Services.CreateScope();
            Assert.True((await scope.ServiceProvider.GetRequiredService<IPaymentProvider>().CaptureAsync(authorization, CancellationToken.None)).Succeeded);
            // Simulates a crash after the provider committed and before the local save.
        }
        Assert.Equal(HttpStatusCode.NoContent, (await customer.PostAsync($"/api/v1/orders/{id}/cancel", null)).StatusCode);
        await SettleAsync(factory, id);
        await SettleAsync(factory, id);
        await using var verify = fixture.CreateContext();
        var payment = await verify.Payments.SingleAsync(x => x.OrderId == id);
        Assert.Equal(PaymentState.Refunded, payment.State);
        Assert.Equal(1, await verify.MockPaymentEffects.CountAsync(x => x.AuthorizationId == payment.AuthorizationId && x.Operation == "Capture"));
        Assert.Equal(1, await verify.MockPaymentEffects.CountAsync(x => x.AuthorizationId == payment.AuthorizationId && x.Operation == "Refund"));
    }

    private async Task SettleAsync(ApiFactory factory, Guid orderId)
    {
        await using var context = fixture.CreateContext();
        var message = await context.OutboxMessages.SingleAsync(x => x.AggregateId == orderId && x.Type == "order.cancelled.v1");
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SettleCancelledPayment>().ExecuteAsync(message.Id, orderId, CancellationToken.None);
    }

    private sealed class AdvancingCaptureProvider(IPaymentProvider inner, TestClock clock) : IPaymentProvider
    {
        public Task<ProviderAuthorization> AuthorizeAsync(Guid actorId, string key, string fingerprint, decimal amount, string currency, CancellationToken ct) =>
            inner.AuthorizeAsync(actorId, key, fingerprint, amount, currency, ct);
        public async Task<ProviderCapture> CaptureAsync(Guid authorizationId, CancellationToken ct)
        {
            var result = await inner.CaptureAsync(authorizationId, ct);
            clock.Advance(TimeSpan.FromMinutes(5));
            return result;
        }
        public Task<ProviderSettlement> SettleAsync(Guid authorizationId, CancellationToken ct) => inner.SettleAsync(authorizationId, ct);
        public Task<ProviderCapture?> FindCaptureAsync(Guid authorizationId, CancellationToken ct) => inner.FindCaptureAsync(authorizationId, ct);
        public Task ReleaseUnusedAuthorizationAsync(Guid authorizationId, CancellationToken ct) => inner.ReleaseUnusedAuthorizationAsync(authorizationId, ct);
    }

    [Fact]
    public async Task CreateAndAccept_AuthorizeThenCaptureExactlyOnce_AndPersistOutboxAtomically()
    {
        await using var factory = new ApiFactory(fixture);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        var key = Guid.NewGuid().ToString("N");
        var response = await CreateAsync(customer, Request(restaurant, product), key);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using (var context = fixture.CreateContext())
        {
            var payment = await context.Payments.SingleAsync(x => x.OrderId == id);
            Assert.Equal(PaymentState.Authorized, payment.State);
            Assert.Equal(6.50m, payment.Amount);
            Assert.True(await context.OutboxMessages.AnyAsync(x => x.AggregateId == id && x.Type == "order.created.v1"));
            Assert.True(await context.OutboxMessages.AnyAsync(x => x.AggregateId == id && x.Type == "payment.authorized.v1"));
        }
        var accept = await Task.WhenAll(owner.PostAsync($"/api/v1/orders/{id}/accept", null),
            owner.PostAsync($"/api/v1/orders/{id}/accept", null));
        Assert.Single(accept, x => x.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(accept, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(customer, Request(restaurant, product), key)).StatusCode);
        await using var verify = fixture.CreateContext();
        var stored = await verify.Payments.SingleAsync(x => x.OrderId == id);
        Assert.Equal(PaymentState.Captured, stored.State);
        Assert.Equal(OrderStatus.Accepted, (await verify.Orders.SingleAsync(x => x.Id == id)).Status);
        Assert.Equal(1, await verify.MockPaymentEffects.CountAsync(x => x.AuthorizationId == stored.AuthorizationId && x.Operation == "Capture" && x.Succeeded));
        var events = await verify.OutboxMessages.Where(x => x.AggregateId == id && x.AggregateSequence != null)
            .OrderBy(x => x.AggregateSequence).ToArrayAsync();
        Assert.Equal(new[] { "order.created.v1", "order.accepted.v1" }, events.Select(x => x.Type));
        Assert.Equal(new long?[] { 1, 2 }, events.Select(x => x.AggregateSequence));
        Assert.All(await verify.OutboxMessages.Where(x => x.AggregateId == id).ToArrayAsync(), message =>
        {
            Assert.DoesNotContain("latitude", message.PayloadJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("longitude", message.PayloadJson, StringComparison.OrdinalIgnoreCase);
        });
        using var scope = factory.Services.CreateScope();
        // Orphan cleanup must never void an attached order's authorization.
        await scope.ServiceProvider.GetRequiredService<IPaymentProvider>().ReleaseUnusedAuthorizationAsync(stored.AuthorizationId, CancellationToken.None);
        var settlements = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var concurrent = factory.Services.CreateScope();
            return await concurrent.ServiceProvider.GetRequiredService<IPaymentProvider>().SettleAsync(stored.AuthorizationId, CancellationToken.None);
        }));
        Assert.All(settlements, x => Assert.Equal(ProviderPaymentState.Refunded, x.State));
        Assert.Equal(1, await verify.MockPaymentEffects.CountAsync(x => x.AuthorizationId == stored.AuthorizationId && x.Operation == "Refund"));
    }

    [Theory]
    [InlineData("Declined", HttpStatusCode.PaymentRequired)]
    [InlineData("Transient", HttpStatusCode.ServiceUnavailable)]
    public async Task AuthorizationFailure_DoesNotPersistOrderOrPayment(string outcome, HttpStatusCode expected)
    {
        await using var factory = new ApiFactory(fixture);
        factory.Overrides["MockPayments:AuthorizeOutcome"] = outcome;
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        var response = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        Assert.Equal(expected, response.StatusCode);
        await using var context = fixture.CreateContext();
        Assert.False(await context.Orders.AnyAsync(x => x.RestaurantId == restaurant));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationConsumer_VoidsOrRefunds_AndDeduplicatesReplay(bool accepted)
    {
        await using var factory = new ApiFactory(fixture);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        using var admin = await ClientAsync(factory, "Admin");
        var created = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        if (accepted) Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/orders/{id}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/orders/{id}/cancel", null)).StatusCode);
        Guid messageId;
        await using (var context = fixture.CreateContext())
            messageId = (await context.OutboxMessages.SingleAsync(x => x.AggregateId == id && x.Type == "order.cancelled.v1")).Id;
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SettleCancelledPayment>().ExecuteAsync(messageId, id, CancellationToken.None);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SettleCancelledPayment>().ExecuteAsync(messageId, id, CancellationToken.None);
        await using var verify = fixture.CreateContext();
        var payment = await verify.Payments.SingleAsync(x => x.OrderId == id);
        Assert.Equal(accepted ? PaymentState.Refunded : PaymentState.Voided, payment.State);
        Assert.Equal(1, await verify.MockPaymentEffects.CountAsync(x => x.AuthorizationId == payment.AuthorizationId
            && x.Operation == (accepted ? "Refund" : "Void") && x.Succeeded));
        Assert.Equal(1, await verify.InboxMessages.CountAsync(x => x.MessageId == messageId && x.Consumer == "payments.cancelled.v1"));
    }
}
