using Testcontainers.RabbitMq;

namespace OrderFleet.IntegrationTests;

public sealed class RabbitFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer container = new RabbitMqBuilder("rabbitmq:4.2-management")
        .WithUsername("orderfleet_tests").WithPassword(Guid.NewGuid().ToString("N"))
        .WithEnvironment("RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS", "+S 2:2").Build();
    internal string ConnectionString => container.GetConnectionString();
    public async Task InitializeAsync() => await container.StartAsync();
    internal Task StopAsync() => container.StopAsync();
    internal async Task StartAsync()
    {
        await container.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                var factory = new RabbitMQ.Client.ConnectionFactory
                {
                    Uri = new Uri(ConnectionString),
                    RequestedConnectionTimeout = TimeSpan.FromSeconds(2),
                    AutomaticRecoveryEnabled = false
                };
                await using var connection = await factory.CreateConnectionAsync(timeout.Token);
                return;
            }
            catch when (!timeout.IsCancellationRequested) { await Task.Delay(100, timeout.Token); }
        }
    }
    public async Task DisposeAsync() => await container.DisposeAsync();
}
