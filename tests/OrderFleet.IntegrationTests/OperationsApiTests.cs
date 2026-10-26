using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderFleet.Application.Messaging;
using OrderFleet.Infrastructure.Messaging;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class OperationsApiTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task MissingIdempotencyKey_ExplainsMaximumLength()
    {
        await using var factory = new ApiFactory(fixture);
        using var admin = await ClientAsync(factory, "Admin");
        var response = await admin.PostAsync($"/api/v1/operations/outbox/{Guid.NewGuid()}/retry", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("An Idempotency-Key of at most 100 characters is required.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task DatabaseOutage_DoesNotBreakLiveness_AndReadinessFailsWithinBound()
    {
        await using var factory = new ApiFactory(fixture);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        await fixture.StopAsync();
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally { await fixture.RestartAsync(); }
    }

    [Fact]
    public async Task RevokedAdminRole_DeniesOperationalAuthorityEvenWithExistingToken()
    {
        await using var factory = new ApiFactory(fixture);
        using var admin = await ClientAsync(factory, "Admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/operations/summary")).StatusCode);
        var actor = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetGuid();
        await using (var context = fixture.CreateContext())
        {
            context.UserRoles.RemoveRange(await context.UserRoles.Where(x => x.UserId == actor).ToArrayAsync());
            await context.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/operations/summary")).StatusCode);
    }

    [Fact]
    public async Task DeadOutboxRetry_IsConcurrentIdempotentAudited_AndCannotStealActiveLease()
    {
        await using var factory = new ApiFactory(fixture);
        using var admin = await ClientAsync(factory, "Admin");
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var leasedId = Guid.NewGuid();
        await using (var context = fixture.CreateContext())
        {
            foreach (var target in new[] { id, leasedId })
                context.OutboxMessages.Add(new(new(target, "order.created.v1", 1, now, Guid.NewGuid(), null, "{}", 1)));
            await context.SaveChangesAsync();
        }
        var store = new PostgresOutboxStore(new TestContextFactory(fixture));
        var claims = await store.ClaimAsync(100, now, TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.True(await store.FailAsync(claims.Single(x => x.Message.MessageId == id), now, "BrokerUnavailable", 1, CancellationToken.None));
        var key = Guid.NewGuid().ToString("N");
        var replies = await Task.WhenAll(RetryAsync(admin, $"/api/v1/operations/outbox/{id}/retry", key),
            RetryAsync(admin, $"/api/v1/operations/outbox/{id}/retry", key));
        Assert.All(replies, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
        Assert.Equal(HttpStatusCode.Conflict, (await RetryAsync(admin, $"/api/v1/operations/outbox/{leasedId}/retry", key)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await RetryAsync(admin, $"/api/v1/operations/outbox/{leasedId}/retry", Guid.NewGuid().ToString("N"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/operations/outbox/{id}/retry", null)).StatusCode);
        await using var verify = fixture.CreateContext();
        var retried = await verify.OutboxMessages.SingleAsync(x => x.Id == id);
        Assert.Null(retried.DeadLetteredAtUtc);
        Assert.Null(retried.LeaseId);
        Assert.Equal(0, retried.Attempts);
        Assert.Equal(1, await verify.OperationAudits.CountAsync(x => x.TargetId == id && x.Action == "RetryOutbox"));
        var audit = await verify.OperationAudits.SingleAsync(x => x.TargetId == id && x.Action == "RetryOutbox");
        Assert.Equal(1, audit.PreviousAttempts);
        Assert.Equal("BrokerUnavailable", audit.PreviousOutcome);
        Assert.Equal(1, await verify.OperationRequests.CountAsync(x => x.TargetId == id && x.Operation == "RetryOutbox"));
        var untouched = await verify.OutboxMessages.SingleAsync(x => x.Id == leasedId);
        Assert.Equal(claims.Single(x => x.Message.MessageId == leasedId).LeaseId, untouched.LeaseId);
    }

    [Fact]
    public async Task OperatorDispatchRetry_ResetsExhaustionOnce_AndCanResumePaidOrder()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var factory = new ApiFactory(fixture, clock);
        factory.Overrides["Dispatch:MaxAttempts"] = "1";
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await DispatchConcurrencyTests.CatalogForDispatchAsync(owner, 70);
        var id = await DispatchConcurrencyTests.AcceptedAsync(factory, owner, restaurant, product);
        Assert.Equal(OrderFleet.Application.Dispatch.DispatchOutcome.Exhausted, await DispatchConcurrencyTests.DispatchAsync(factory, id));
        using var admin = await ClientAsync(factory, "Admin");
        var key = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.NoContent, (await RetryAsync(admin, $"/api/v1/operations/orders/{id}/retry-dispatch", key)).StatusCode);
        using var courier = await DispatchConcurrencyTests.CourierAsync(factory, 70);
        Assert.Equal(OrderFleet.Application.Dispatch.DispatchOutcome.Assigned, await DispatchConcurrencyTests.DispatchAsync(factory, id));
        Assert.Equal(HttpStatusCode.NoContent, (await RetryAsync(admin, $"/api/v1/operations/orders/{id}/retry-dispatch", key)).StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.OperationAudits.CountAsync(x => x.TargetId == id && x.Action == "RetryDispatch"));
        Assert.Equal("Assigned", (await verify.DispatchJobs.SingleAsync(x => x.OrderId == id)).Status);
        Assert.Equal(1, (await verify.DispatchJobs.SingleAsync(x => x.OrderId == id)).AssignmentCount);
    }

    private static async Task<HttpResponseMessage> RetryAsync(HttpClient client, string path, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Health_IsAnonymousRedacted_AndReadinessRefusesPausedWorkers()
    {
        await using var factory = new ApiFactory(fixture);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        var body = await ready.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionString", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OperationalRead_RequiresAdmin_AndNeverReturnsPayloadsOrCredentials()
    {
        await using var factory = new ApiFactory(fixture);
        using var customer = await ClientAsync(factory, "Customer");
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/operations/summary")).StatusCode);
        using var admin = await ClientAsync(factory, "Admin");
        var response = await admin.GetAsync("/api/v1/operations/summary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(summary.TryGetProperty("outboxPending", out _));
        var dead = await admin.GetAsync("/api/v1/operations/outbox?state=dead&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, dead.StatusCode);
        Assert.DoesNotContain("payload", await dead.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}
