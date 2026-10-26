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
    [Fact]
    public async Task SixCandidateQueries_AreExplainedOnOneHundredThousandOrdersAndJobs()
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
            await seed.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(root.FullName, "tools", "seed-recovery-plans.sql")));
            Assert.Equal(100000, await seed.Orders.CountAsync());
            Assert.Equal(100000, await seed.DispatchJobs.CountAsync());
        }
        var capture = new CaptureQueries();
        var contexts = new PlanContexts(database.Fixture.ConnectionString, capture);
        using var services = new ServiceCollection().BuildServiceProvider();
        var recovery = new EfRecoveryStore(contexts, services.GetRequiredService<IServiceScopeFactory>());
        var now = DateTimeOffset.UtcNow;
        Assert.NotEmpty(await recovery.ExpiredOrdersAsync(now, 20, CancellationToken.None));
        Assert.NotEmpty(await recovery.ExpiredAssignmentsAsync(now, 20, CancellationToken.None));
        Assert.NotEmpty(await recovery.DueDispatchAsync(now, 20, CancellationToken.None));
        Assert.NotEmpty(await recovery.UnsettledCancellationsAsync(now, 20, CancellationToken.None));
        Assert.NotEmpty(await recovery.UncertainPaymentsAsync(now, 20, CancellationToken.None));
        Assert.NotEmpty(await recovery.OrphanAuthorizationsAsync(now.AddMinutes(-5), now, 20, CancellationToken.None));
        Assert.Equal(6, capture.Queries.Count);
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
