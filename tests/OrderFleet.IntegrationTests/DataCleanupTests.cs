using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using OrderFleet.Application.Dispatch;
using OrderFleet.Application.Identity;
using OrderFleet.Application.Messaging;
using OrderFleet.Application.Operations;
using OrderFleet.Application.Orders;
using OrderFleet.Application.Payments;
using OrderFleet.Application.Realtime;
using OrderFleet.Application.Recovery;
using OrderFleet.Domain.Orders;
using OrderFleet.Infrastructure.Clock;
using OrderFleet.Infrastructure.Maintenance;
using OrderFleet.Infrastructure.Messaging;
using OrderFleet.Infrastructure.Persistence;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;
using static OrderFleet.IntegrationTests.DispatchConcurrencyTests;

namespace OrderFleet.IntegrationTests;


[Trait("Category", "PostgreSQL")]
public sealed class DataCleanupTests : IAsyncLifetime
{
    private readonly PostgresFixture fixture = new();
    private readonly RabbitFixture broker = new();
    private bool brokerStarted;
    private static readonly DateTimeOffset Now = Normalize(DateTimeOffset.UtcNow);
    private static DateTimeOffset Normalize(DateTimeOffset value) => value.AddTicks(-value.Ticks % 10);
    public Task InitializeAsync() => fixture.InitializeAsync();
    public async Task DisposeAsync() { if (brokerStarted) await broker.DisposeAsync(); await fixture.DisposeAsync(); }

    [Fact]
    public async Task PublishedRetention_IsStrictBatched_AndPreservesPendingLeasedAndUnconsumedFacts()
    {
        var expired = new List<Guid>();
        for (var i = 0; i < 5; i++) expired.Add(await PublishedAsync(Now.AddDays(-8), consumed: true));
        var boundary = await PublishedAsync(Now.AddDays(-7), consumed: true);
        var newer = await PublishedAsync(Now.AddDays(-6), consumed: true);
        var unconsumed = await PublishedAsync(Now.AddDays(-8), consumed: false);
        var leased = await InsertAsync(Now.AddDays(-20));
        var leases = new PostgresOutboxStore(new TestContextFactory(fixture));
        Assert.Equal(leased.MessageId, Assert.Single(await leases.ClaimAsync(1, Now, TimeSpan.FromSeconds(60), CancellationToken.None)).Message.MessageId);
        var pending = await InsertAsync(Now.AddDays(-20));
        var recorder = new DeleteRecorder();
        var cleanup = Cleanup(new RecordedContexts(fixture, recorder));
        var result = await cleanup.RunAsync(CancellationToken.None);
        Assert.Equal(5, result.PublishedOutbox);
        Assert.Equal(new[] { 2, 2, 1 }, recorder.DeletedRows);
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.OutboxMessages.AnyAsync(x => expired.Contains(x.Id)));
        foreach (var id in new[] { boundary, newer, unconsumed, leased.MessageId, pending.MessageId })
            Assert.True(await verify.OutboxMessages.AnyAsync(x => x.Id == id));
        Assert.Equal(7, await verify.InboxMessages.CountAsync());
    }

    [Fact]
    public async Task RecoveryRetention_UsesResolvedTime_AndKeepsUnresolvedAndBoundaryRows()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var recovery = new EfRecoveryStore(new TestContextFactory(fixture), services.GetRequiredService<IServiceScopeFactory>());
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids) await recovery.RecordFailureAsync(RecoveryWork.DispatchRetry, id, Now.AddDays(-50), "DependencyOrConflict", 10, CancellationToken.None);
        await recovery.RecordResolvedAsync(RecoveryWork.DispatchRetry, ids[0], Now.AddDays(-31), CancellationToken.None);
        await recovery.RecordResolvedAsync(RecoveryWork.DispatchRetry, ids[1], Now.AddDays(-30), CancellationToken.None);
        await recovery.RecordResolvedAsync(RecoveryWork.DispatchRetry, ids[2], Now.AddDays(-29), CancellationToken.None);
        Assert.Equal(1, (await Cleanup().RunAsync(CancellationToken.None)).ResolvedFailures);
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.RecoveryWorkFailures.AnyAsync(x => x.ResourceId == ids[0]));
        Assert.Equal(3, await verify.RecoveryWorkFailures.CountAsync());
        Assert.Null((await verify.RecoveryWorkFailures.SingleAsync(x => x.ResourceId == ids[3])).ResolvedAtUtc);
    }

    [Fact]
    public async Task FinancialAuditRequestsInboxAndActiveWork_AreRetainedIndefinitely()
    {
        var clock = new TestClock(Now);
        await using var factory = new ApiFactory(fixture, clock);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        using var customer = await ClientAsync(factory, "Customer");
        using var admin = await ClientAsync(factory, "Admin");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, -80);
        var customerActor = ActorFor(customer, OrderFleetRoles.Customer);
        var ownerActor = ActorFor(owner, OrderFleetRoles.RestaurantOwner);
        var adminActor = ActorFor(admin, OrderFleetRoles.Admin);
        clock.Advance(TimeSpan.FromDays(-40));
        var orders = new List<Guid>();
        var replayKey = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 2; i++)
        {
            using var scope = factory.Services.CreateScope();
            var id = await scope.ServiceProvider.GetRequiredService<CreateOrder>().ExecuteAsync(customerActor,
                new(restaurant, [new(product, 1)], 24.7m, 46.7m), Guid.NewGuid().ToString("N"), CancellationToken.None);
            orders.Add(id);
            await scope.ServiceProvider.GetRequiredService<OrderCommands>().AcceptAsync(id, ownerActor, CancellationToken.None);
            Assert.Equal(DispatchOutcome.NoCourier, await DispatchAsync(factory, id));
            if (i == 0) await scope.ServiceProvider.GetRequiredService<OrderCommands>().CancelAsync(id, ownerActor, CancellationToken.None);
            else await scope.ServiceProvider.GetRequiredService<OperateOrderFleet>().RetryDispatchAsync(adminActor, id, replayKey, CancellationToken.None);
        }
        await broker.InitializeAsync(); brokerStarted = true;
        var settings = new RabbitOptions { Uri = broker.ConnectionString, Prefix = "retention-" + Guid.NewGuid().ToString("N") };
        await using var publisher = new RabbitEventPublisher(Options.Create(settings));
        var store = new PostgresOutboxStore(new TestContextFactory(fixture));
        foreach (var lease in await store.ClaimAsync(100, clock.GetUtcNow(), TimeSpan.FromSeconds(60), CancellationToken.None))
        {
            await publisher.PublishAsync(lease.Message, CancellationToken.None);
            Assert.True(await store.CompleteAsync(lease, clock.GetUtcNow(), CancellationToken.None));
            using var scope = factory.Services.CreateScope();
            if (lease.Message.Type.StartsWith("payment.", StringComparison.Ordinal))
                await scope.ServiceProvider.GetRequiredService<ArchiveFinancialEvent>().HandleAsync(lease.Message, CancellationToken.None);
            else
            {
                await scope.ServiceProvider.GetRequiredService<RealtimeEventHandler>().HandleAsync(lease.Message, CancellationToken.None);
                if (lease.Message.Type is "order.accepted.v1" or "order.assignment-released.v1")
                    await scope.ServiceProvider.GetRequiredService<DispatchEventHandler>().HandleAsync(lease.Message, CancellationToken.None);
            }
        }
        clock.Advance(TimeSpan.FromDays(40));
        var before = await ProtectedCountsAsync();
        var result = await Cleanup().RunAsync(CancellationToken.None);
        Assert.True(result.PublishedOutbox > 0);
        Assert.Equal(1, result.DispatchAttempts);
        Assert.Equal(before, await ProtectedCountsAsync());
        await using var verify = fixture.CreateContext();
        Assert.True(await verify.DispatchAttempts.AnyAsync(x => x.OrderId == orders[1]));
        Assert.False(await verify.DispatchAttempts.AnyAsync(x => x.OrderId == orders[0]));
        Assert.True(await verify.OutboxMessages.AnyAsync(x => x.AggregateId == orders[0] && x.Type == "order.cancelled.v1"));
        using var replay = factory.Services.CreateScope();
        await replay.ServiceProvider.GetRequiredService<OperateOrderFleet>().RetryDispatchAsync(adminActor, orders[1], replayKey, CancellationToken.None);
        Assert.Equal(before, await ProtectedCountsAsync());
    }

    [Fact]
    public async Task ConcurrentRelay_KeepsItsConfirmedButNotCompletedLeaseAndNewPublication()
    {
        await PublishedAsync(Now.AddDays(-8), consumed: true);
        var pending = await InsertAsync(Now.AddDays(-20));
        await broker.InitializeAsync(); brokerStarted = true;
        var settings = new RabbitOptions { Uri = broker.ConnectionString, Prefix = "cleanup-race-" + Guid.NewGuid().ToString("N") };
        await using var publisher = new RabbitEventPublisher(Options.Create(settings));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var relay = new OutboxRelay(new PostgresOutboxStore(new TestContextFactory(fixture)), new HeldPublisher(publisher, entered, resume),
            new SystemClock(new TestClock(Now)), Options.Create(settings), Microsoft.Extensions.Logging.Abstractions.NullLogger<OutboxRelay>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var publishing = relay.RunBatchAsync(timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            Assert.Equal(1, (await Cleanup().RunAsync(timeout.Token)).PublishedOutbox);
            await using var during = fixture.CreateContext();
            var leased = await during.OutboxMessages.SingleAsync(x => x.Id == pending.MessageId, timeout.Token);
            Assert.NotNull(leased.LeaseId);
            Assert.Null(leased.PublishedAtUtc);
        }
        finally { resume.TrySetResult(); }
        Assert.Equal(1, await publishing);
        Assert.Equal(0, (await Cleanup().RunAsync(timeout.Token)).PublishedOutbox);
        await using var verify = fixture.CreateContext();
        Assert.NotNull((await verify.OutboxMessages.SingleAsync(x => x.Id == pending.MessageId, timeout.Token)).PublishedAtUtc);
    }

    [Fact]
    public async Task ReopenedRecoveryFailure_IsNotDeletedAfterCleanupWaitsForItsLock()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var name = "cleanup-contention-" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = name }.ConnectionString;
        var contexts = new NamedContexts(connection);
        var recovery = new EfRecoveryStore(contexts, services.GetRequiredService<IServiceScopeFactory>());
        var id = Guid.NewGuid();
        await recovery.RecordFailureAsync(RecoveryWork.DispatchRetry, id, Now.AddDays(-50), "DependencyOrConflict", 10, CancellationToken.None);
        await recovery.RecordResolvedAsync(RecoveryWork.DispatchRetry, id, Now.AddDays(-31), CancellationToken.None);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"RecoveryWorkFailures\" WHERE \"ResourceId\" = {id} FOR UPDATE");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var reopening = recovery.RecordFailureAsync(RecoveryWork.DispatchRetry, id, Now, "DependencyOrConflict", 10, deadline.Token);
        Task<CleanupResult>? sweep = null;
        try
        {
            await WaitForLocksAsync(name, 1, deadline.Token);
            sweep = Cleanup(contexts).RunAsync(deadline.Token);
            await WaitForLocksAsync(name, 2, deadline.Token);
            await transaction.CommitAsync(deadline.Token);
            await reopening;
            Assert.Equal(0, (await sweep).ResolvedFailures);
        }
        finally
        {
            if (blocker.Database.CurrentTransaction is not null) await transaction.RollbackAsync();
            await reopening;
            if (sweep is not null) await sweep;
        }
        await using var verify = fixture.CreateContext();
        var failure = await verify.RecoveryWorkFailures.SingleAsync(x => x.ResourceId == id);
        Assert.Null(failure.ResolvedAtUtc);
        Assert.Equal(1, failure.Attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HostedCleanup_RespectsEnabledFlag(bool enabled)
    {
        var id = await PublishedAsync(Now.AddDays(-8), consumed: true);
        await using var factory = new ApiFactory(fixture, new TestClock(Now));
        factory.Overrides["Cleanup:Enabled"] = enabled.ToString();
        factory.Overrides["Cleanup:PollSeconds"] = "1";
        using var client = factory.CreateClient();
        var worker = Assert.Single(factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<CleanupWorker>());
        if (enabled)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                await using var check = fixture.CreateContext();
                if (!await check.OutboxMessages.AnyAsync(x => x.Id == id, deadline.Token)) break;
                await Task.Delay(25, deadline.Token);
            }
        }
        else
        {
            await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(worker.ExecuteTask.IsCompletedSuccessfully);
            await using var check = fixture.CreateContext();
            Assert.True(await check.OutboxMessages.AnyAsync(x => x.Id == id));
        }
    }

    private async Task WaitForLocksAsync(string name, int count, CancellationToken ct)
    {
        await using var observer = fixture.CreateContext();
        while (await observer.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM pg_stat_activity WHERE application_name = {name} AND wait_event_type = 'Lock' AND state = 'active'").SingleAsync(ct) != count)
            await Task.Delay(25, ct);
    }

    [Fact]
    public async Task CancelledCleanup_PerformsNoDeletion()
    {
        var id = await PublishedAsync(Now.AddDays(-8), consumed: true);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Cleanup().RunAsync(cancelled.Token));
        await using var verify = fixture.CreateContext();
        Assert.True(await verify.OutboxMessages.AnyAsync(x => x.Id == id));
    }

    private ExpiredDataCleanup Cleanup(IDbContextFactory<OrderFleetDbContext>? contexts = null) => new(contexts ?? new TestContextFactory(fixture),
        new SystemClock(new TestClock(Now)), Options.Create(new CleanupOptions { BatchSize = 2 }));
    private async Task<Guid> PublishedAsync(DateTimeOffset published, bool consumed)
    {
        var fact = await InsertAsync(published);
        var store = new PostgresOutboxStore(new TestContextFactory(fixture));
        var lease = Assert.Single(await store.ClaimAsync(1, published, TimeSpan.FromSeconds(60), CancellationToken.None));
        Assert.Equal(fact.MessageId, lease.Message.MessageId);
        Assert.True(await store.CompleteAsync(lease, published, CancellationToken.None));
        if (consumed)
        {
            await using var context = fixture.CreateContext();
            context.InboxMessages.Add(new(fact.MessageId, "realtime.v1", published)); await context.SaveChangesAsync();
        }
        return fact.MessageId;
    }
    private async Task<IntegrationEvent> InsertAsync(DateTimeOffset occurred)
    {
        var order = Order.Create(Guid.NewGuid(), Guid.NewGuid(), [new(Guid.NewGuid(), "Tea", 1m, 1)], new(10, 10, 100m, 1000m), occurred);
        var fact = IntegrationEvents.FromOrder(Assert.Single(order.DomainEvents)) with { AggregateSequence = 1 };
        await using var context = fixture.CreateContext(); context.OutboxMessages.Add(new(fact)); await context.SaveChangesAsync(); return fact;
    }
    private static Actor ActorFor(HttpClient client, string role) => new(Guid.Parse(new JwtSecurityTokenHandler()
        .ReadJwtToken(client.DefaultRequestHeaders.Authorization!.Parameter!).Subject), new HashSet<string> { role });
    private async Task<int[]> ProtectedCountsAsync()
    {
        await using var context = fixture.CreateContext();
        return [await context.Orders.CountAsync(), await context.Payments.CountAsync(), await context.MockPaymentAuthorizations.CountAsync(),
            await context.MockPaymentEffects.CountAsync(), await context.OrderAuditEntries.CountAsync(), await context.OperationAudits.CountAsync(),
            await context.OrderCreationRequests.CountAsync(), await context.OperationRequests.CountAsync(), await context.InboxMessages.CountAsync(),
            await context.FinancialAuditRecords.CountAsync(), await context.CourierAssignments.CountAsync()];
    }
    private sealed class HeldPublisher(IEventPublisher inner, TaskCompletionSource entered, TaskCompletionSource resume) : IEventPublisher
    {
        public async Task PublishAsync(IntegrationEvent message, CancellationToken ct)
        { await inner.PublishAsync(message, ct); entered.TrySetResult(); await resume.Task.WaitAsync(ct); }
    }
    private sealed class DeleteRecorder : DbCommandInterceptor
    {
        internal List<int> DeletedRows { get; } = [];
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data, int result, CancellationToken ct = default)
        { if (command.CommandText.StartsWith("DELETE", StringComparison.Ordinal)) DeletedRows.Add(result); return ValueTask.FromResult(result); }
    }
    private sealed class RecordedContexts(PostgresFixture postgres, DeleteRecorder recorder) : IDbContextFactory<OrderFleetDbContext>
    {
        public OrderFleetDbContext CreateDbContext() => new(new DbContextOptionsBuilder<OrderFleetDbContext>()
            .UseNpgsql(postgres.ConnectionString, x => x.UseNetTopologySuite()).AddInterceptors(recorder).Options);
    }
    private sealed class NamedContexts(string connection) : IDbContextFactory<OrderFleetDbContext>
    {
        public OrderFleetDbContext CreateDbContext() => new(new DbContextOptionsBuilder<OrderFleetDbContext>()
            .UseNpgsql(connection, x => x.UseNetTopologySuite()).Options);
    }
}
