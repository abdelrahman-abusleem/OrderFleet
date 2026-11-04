using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OrderFleet.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class RecoveryQueryPlanTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(100000, false)]
    [InlineData(300000, false)]
    [InlineData(100000, true)]
    public async Task RecoveryAndMetrics_AreExplainedOnHistoricalOrdersAndJobs(int rows, bool idle)
    {
        await using var database = new OwnedPostgres();
        await database.Fixture.InitializeAsync();
        await using (var seed = database.Fixture.CreateContext())
        {
            seed.Database.SetCommandTimeout(120);
            // This path is source-controlled benchmark input, never a developer DB.
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "OrderFleet.slnx"))) root = root.Parent;
            Assert.NotNull(root);
            var sql = await File.ReadAllTextAsync(Path.Combine(root.FullName, "tools", "seed-recovery-plans.sql"));
            sql = sql.Replace("generate_series(1, 100000)", idle ? $"generate_series(501, {rows + 500})" : $"generate_series(1, {rows})", StringComparison.Ordinal);
            if (idle) sql = sql.Replace("generate_series(1, 100)", "generate_series(1, 0)", StringComparison.Ordinal);
            await seed.Database.ExecuteSqlRawAsync(sql);
            await seed.Database.ExecuteSqlRawAsync("""
                UPDATE "OutboxMessages" SET "PublishedAtUtc" = NULL, "DeadLetteredAtUtc" = now(), "LastErrorCode" = 'InvalidEnvelope'
                WHERE "Sequence" IN (SELECT "Sequence" FROM "OutboxMessages" ORDER BY "Sequence" DESC LIMIT 100);
                ANALYZE;
                """);
            Assert.Equal(rows, await seed.Orders.CountAsync());
            Assert.Equal(rows, await seed.DispatchJobs.CountAsync());
        }
        var capture = new CaptureQueries();
        var contexts = new PlanContexts(database.Fixture.ConnectionString, capture);
        using var services = new ServiceCollection().BuildServiceProvider();
        var recovery = new EfRecoveryStore(contexts, services.GetRequiredService<IServiceScopeFactory>());
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(!idle, (await recovery.ExpiredOrdersAsync(now, 20, CancellationToken.None)).Count > 0);
        Assert.Equal(!idle, (await recovery.ExpiredAssignmentsAsync(now, 20, CancellationToken.None)).Count > 0);
        Assert.Equal(!idle, (await recovery.DueDispatchAsync(now, 20, CancellationToken.None)).Count > 0);
        Assert.Equal(!idle, (await recovery.UnsettledCancellationsAsync(now, 20, CancellationToken.None)).Count > 0);
        Assert.Equal(!idle, (await recovery.UncertainPaymentsAsync(now, 20, CancellationToken.None)).Count > 0);
        Assert.Equal(!idle, (await recovery.OrphanAuthorizationsAsync(now.AddMinutes(-5), now, 20, CancellationToken.None)).Count > 0);
        Assert.Equal(6, capture.Queries.Count);
        await using (var metricsContext = contexts.CreateDbContext())
        {
            var summary = await new EfOperationsStore(metricsContext).SummaryAsync(now, CancellationToken.None);
            Assert.Equal(100, summary.OutboxDead);
            Assert.Equal(0, summary.OutboxPending);
        }
        output.WriteLine($"dataset: rows={rows}, idle={idle}; first six queries are recovery, remaining queries are actual operations sampling");
        await using var connection = new NpgsqlConnection(database.Fixture.ConnectionString);
        await connection.OpenAsync();
        foreach (var query in capture.Queries)
        {
            await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + query.Sql, connection);
            command.Parameters.AddRange(query.Parameters);
            output.WriteLine(query.Sql);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) output.WriteLine(reader.GetString(0));
        }
    }

    private sealed class OwnedPostgres : IAsyncDisposable
    {
        internal PostgresFixture Fixture { get; } = new();
        public ValueTask DisposeAsync() => new(Fixture.DisposeAsync());
    }
    private sealed class PlanContexts(string connection, CaptureQueries capture) : IDbContextFactory<OrderFleetDbContext>
    {
        public OrderFleetDbContext CreateDbContext() => new(new DbContextOptionsBuilder<OrderFleetDbContext>()
            .UseNpgsql(connection, p => p.UseNetTopologySuite()).AddInterceptors(capture).Options);
    }
    private sealed record Query(string Sql, NpgsqlParameter[] Parameters);
    private sealed class CaptureQueries : DbCommandInterceptor
    {
        internal List<Query> Queries { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            Queries.Add(new(command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone()).ToArray()));
            return ValueTask.FromResult(result);
        }
    }
}
