using Microsoft.EntityFrameworkCore;
using OrderFleet.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace OrderFleet.IntegrationTests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgis/postgis:17-3.5")
        .WithDatabase("orderfleet_tests")
        .WithUsername("orderfleet_tests")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();

    internal string ConnectionString => container.GetConnectionString();

    public OrderFleetDbContext CreateContext() => new(
        new DbContextOptionsBuilder<OrderFleetDbContext>()
            .UseNpgsql(container.GetConnectionString(), postgres => postgres.UseNetTopologySuite()).Options);

    public async Task InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await container.StartAsync(timeout.Token);
        await using var context = CreateContext();
        await context.Database.MigrateAsync(timeout.Token);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();
}
