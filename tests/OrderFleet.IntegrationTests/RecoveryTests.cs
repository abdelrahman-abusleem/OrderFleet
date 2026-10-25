using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderFleet.Application.Recovery;
using OrderFleet.Application.Payments;
using OrderFleet.Domain.Couriers;
using OrderFleet.Domain.Orders;
using OrderFleet.Domain.Payments;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;
using static OrderFleet.IntegrationTests.DispatchConcurrencyTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class RecoveryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task PickedUpOrder_IsNeverReleasedByExpiredReservationTimer()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var factory = new ApiFactory(fixture, clock);
        using var courier = await CourierAsync(factory, -50);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, -50);
        var id = await AcceptedAsync(factory, owner, restaurant, product);
        Assert.Equal(OrderFleet.Application.Dispatch.DispatchOutcome.Assigned, await DispatchAsync(factory, id));
        var assignment = (await courier.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}")).GetProperty("assignmentId").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await courier.PostAsJsonAsync($"/api/v1/orders/{id}/pickup", new { assignmentId = assignment })).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(30));
        await RunAsync(factory);
        await using var verify = fixture.CreateContext();
        var order = await verify.Orders.SingleAsync(x => x.Id == id);
        Assert.Equal(OrderStatus.PickedUp, order.Status);
        Assert.Equal(assignment, order.AssignmentId);
        Assert.Equal(CourierStatus.Busy, (await verify.Couriers.SingleAsync(x => x.Id == order.CourierId)).Status);
        var history = await verify.CourierAssignments.SingleAsync(x => x.Id == assignment);
        Assert.NotNull(history.PickedUpAtUtc);
        Assert.Null(history.ReleasedAtUtc);
    }

    [Fact]
    public async Task UnknownCapture_ReconcilesWithoutNewCharge_AndExpiredOrderRefunds()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var factory = new ApiFactory(fixture, clock);
        factory.Overrides["MockPayments:CaptureOutcome"] = "Unknown";
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, -10);
        using var customer = await ClientAsync(factory, "Customer");
        var response = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await owner.PostAsync($"/api/v1/orders/{id}/accept", null)).StatusCode);
        await using var restarted = new ApiFactory(fixture, clock);
        await RunAsync(restarted);
        await using (var context = fixture.CreateContext())
        {
            Assert.Equal(PaymentState.Captured, (await context.Payments.SingleAsync(x => x.OrderId == id)).State);
            Assert.Equal(OrderStatus.Created, (await context.Orders.SingleAsync(x => x.Id == id)).Status);
        }
        clock.Advance(TimeSpan.FromMinutes(5));
        await RunAsync(restarted);
        await using var verify = fixture.CreateContext();
        var payment = await verify.Payments.SingleAsync(x => x.OrderId == id);
        Assert.Equal(PaymentState.Refunded, payment.State);
        Assert.Equal(1, await verify.MockPaymentEffects.CountAsync(x => x.AuthorizationId == payment.AuthorizationId && x.Operation == "Capture"));
        Assert.Equal(1, await verify.MockPaymentEffects.CountAsync(x => x.AuthorizationId == payment.AuthorizationId && x.Operation == "Refund"));
    }

    [Fact]
    public async Task OrphanAuthorization_RecoversAfterGrace_WithoutRequiringOrderCommit()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var factory = new ApiFactory(fixture, clock);
        Guid authorization;
        using (var scope = factory.Services.CreateScope())
            authorization = (await scope.ServiceProvider.GetRequiredService<IPaymentProvider>().AuthorizeAsync(Guid.NewGuid(),
                Guid.NewGuid().ToString("N"), new string('A', 64), 1m, "SAR", CancellationToken.None)).Id;
        await RunAsync(factory);
        await using (var context = fixture.CreateContext())
            Assert.Equal(ProviderPaymentState.Authorized, (await context.MockPaymentAuthorizations.SingleAsync(x => x.Id == authorization)).State);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Task.WhenAll(RunAsync(factory), RunAsync(factory));
        await using var verify = fixture.CreateContext();
        Assert.Equal(ProviderPaymentState.Voided, (await verify.MockPaymentAuthorizations.SingleAsync(x => x.Id == authorization)).State);
        Assert.Equal(1, await verify.MockPaymentEffects.CountAsync(x => x.AuthorizationId == authorization && x.Operation == "Void"));
    }

    [Fact]
    public async Task NoCourierRetries_RespectPersistedBackoffAndThreshold_AcrossHostRestart()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var factory = new ApiFactory(fixture, clock);
        factory.Overrides["Dispatch:MaxAttempts"] = "2";
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, -20);
        var id = await AcceptedAsync(factory, owner, restaurant, product);
        Assert.Equal(OrderFleet.Application.Dispatch.DispatchOutcome.NoCourier, await DispatchAsync(factory, id));
        await RunAsync(factory);
        await using (var context = fixture.CreateContext())
            Assert.Equal(1, (await context.DispatchJobs.SingleAsync(x => x.OrderId == id)).Attempts);
        clock.Advance(TimeSpan.FromSeconds(3));
        await using var restarted = new ApiFactory(fixture, clock);
        restarted.Overrides["Dispatch:MaxAttempts"] = "2";
        await RunAsync(restarted);
        using var courier = await CourierAsync(restarted, -20);
        clock.Advance(TimeSpan.FromSeconds(3));
        await RunAsync(restarted);
        await using var verify = fixture.CreateContext();
        var job = await verify.DispatchJobs.SingleAsync(x => x.OrderId == id);
        Assert.Equal("Exhausted", job.Status);
        Assert.Equal(2, job.Attempts);
        Assert.Equal(OrderStatus.Accepted, (await verify.Orders.SingleAsync(x => x.Id == id)).Status);
        Assert.Equal(2, await verify.DispatchAttempts.CountAsync(x => x.OrderId == id && x.Outcome == "NoCourier"));
    }

    [Fact]
    public async Task PickupAtDeadline_RejectsBeforeWorker_AndRecoveryBoundsReassignmentCycles()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var factory = new ApiFactory(fixture, clock);
        factory.Overrides["Dispatch:MaxAssignments"] = "1";
        using var courier = await CourierAsync(factory, -30);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, -30);
        var id = await AcceptedAsync(factory, owner, restaurant, product);
        Assert.Equal(OrderFleet.Application.Dispatch.DispatchOutcome.Assigned, await DispatchAsync(factory, id));
        var old = (await courier.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}")).GetProperty("assignmentId").GetGuid();
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.Conflict, (await courier.PostAsJsonAsync($"/api/v1/orders/{id}/pickup", new { assignmentId = old })).StatusCode);
        await RunAsync(factory);
        await RunAsync(factory);
        await using var verify = fixture.CreateContext();
        Assert.Equal(OrderStatus.Accepted, (await verify.Orders.SingleAsync(x => x.Id == id)).Status);
        var job = await verify.DispatchJobs.SingleAsync(x => x.OrderId == id);
        Assert.Equal("Exhausted", job.Status);
        Assert.Equal(1, job.AssignmentCount);
        Assert.NotNull((await verify.CourierAssignments.SingleAsync(x => x.Id == old)).ReleasedAtUtc);
        Assert.Equal(CourierStatus.Available, (await verify.Couriers.SingleAsync(x => x.Latitude == -30)).Status);
    }

    [Fact]
    public async Task HostedRecovery_ExpiresAndCompensatesWithoutManualSweep()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var factory = new ApiFactory(fixture, clock);
        factory.Overrides["Recovery:Enabled"] = "true";
        factory.Overrides["Recovery:PollMilliseconds"] = "100";
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, -40);
        using var customer = await ClientAsync(factory, "Customer");
        var response = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        clock.Advance(TimeSpan.FromMinutes(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            await using var context = fixture.CreateContext();
            if (await context.Payments.AnyAsync(x => x.OrderId == id && x.State == PaymentState.Voided, timeout.Token)) break;
            await Task.Delay(50, timeout.Token);
        }
        await using var verify = fixture.CreateContext();
        Assert.Equal(CancellationReason.AcceptanceTimedOut, (await verify.Orders.SingleAsync(x => x.Id == id)).CancelReason);
    }

    [Fact]
    public async Task ExpiredAcceptance_SurvivesHostRestart_AndDuplicateRecoveryVoidsOnce()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var original = new ApiFactory(fixture, clock);
        using var owner = await ClientAsync(original, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, 10);
        using var customer = await ClientAsync(original, "Customer");
        var response = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        clock.Advance(TimeSpan.FromMinutes(5));
        await using var restarted = new ApiFactory(fixture, clock);
        restarted.Overrides["Ordering:AcceptanceTimeoutMinutes"] = "60";
        await Task.WhenAll(RunAsync(restarted), RunAsync(restarted));
        await using var verify = fixture.CreateContext();
        var order = await verify.Orders.SingleAsync(x => x.Id == id);
        var payment = await verify.Payments.SingleAsync(x => x.OrderId == id);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(CancellationReason.AcceptanceTimedOut, order.CancelReason);
        Assert.Equal(PaymentState.Voided, payment.State);
        Assert.Equal(1, await verify.OutboxMessages.CountAsync(x => x.AggregateId == id && x.Type == "order.cancelled.v1"));
        Assert.Equal(1, await verify.MockPaymentEffects.CountAsync(x => x.AuthorizationId == payment.AuthorizationId && x.Operation == "Void"));
    }

    [Fact]
    public async Task ExpiredAssignment_ReleasesCurrentFence_ReassignsAndRejectsOldTimeout()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var factory = new ApiFactory(fixture, clock);
        using var courier = await CourierAsync(factory, 20);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, 20);
        var id = await AcceptedAsync(factory, owner, restaurant, product);
        Assert.Equal(OrderFleet.Application.Dispatch.DispatchOutcome.Assigned, await DispatchAsync(factory, id));
        var old = (await courier.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}")).GetProperty("assignmentId").GetGuid();
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.NoContent, (await courier.PutAsJsonAsync("/api/v1/couriers/me/location",
            new { latitude = 20m, longitude = 46.7m, observedAt = clock.GetUtcNow() })).StatusCode);
        await using var restarted = new ApiFactory(fixture, clock);
        restarted.Overrides["Dispatch:PickupTimeoutMinutes"] = "60";
        await Task.WhenAll(RunAsync(restarted), RunAsync(restarted));
        await using var verify = fixture.CreateContext();
        var current = await verify.Orders.SingleAsync(x => x.Id == id);
        Assert.Equal(OrderStatus.Assigned, current.Status);
        Assert.NotEqual(old, current.AssignmentId);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<IRecoveryStore>().ReleaseExpiredAssignmentAsync(id, old, clock.GetUtcNow(), CancellationToken.None));
        Assert.Equal(HttpStatusCode.Conflict, (await courier.PostAsJsonAsync($"/api/v1/orders/{id}/pickup", new { assignmentId = old })).StatusCode);
        var profile = await verify.Couriers.SingleAsync(x => x.Id == current.CourierId);
        Assert.Equal(current.AssignmentId, profile.AssignmentId);
        Assert.Equal(CourierStatus.Reserved, profile.Status);
    }

    internal static async Task RunAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RecoverOrderFleet>().ExecuteAsync(CancellationToken.None);
    }
}
