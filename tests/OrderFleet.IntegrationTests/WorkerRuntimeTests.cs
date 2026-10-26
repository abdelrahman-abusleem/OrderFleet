using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrderFleet.Infrastructure.Messaging;
using OrderFleet.Infrastructure.Operations;
using OpenTelemetry.Metrics;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "RabbitMQ")]
public sealed class WorkerRuntimeTests(PostgresFixture postgres, RabbitFixture rabbit) : IClassFixture<PostgresFixture>, IClassFixture<RabbitFixture>
{
    [Fact]
    public async Task RealIdleWorkers_StayHealthy_DetectBrokerFailure_AndRecoverWithNewConnections()
    {
        await using var factory = new ApiFactory(postgres);
        var metrics = new CapturedMetrics();
        factory.CustomizeServices = services => services.AddOpenTelemetry().WithMetrics(builder =>
            builder.AddReader(new PeriodicExportingMetricReader(metrics, 60000)));
        factory.Overrides["Messaging:Enabled"] = "true";
        factory.Overrides["Recovery:Enabled"] = "true";
        factory.Overrides["Messaging:Uri"] = rabbit.ConnectionString;
        factory.Overrides["Messaging:Prefix"] = "runtime" + Guid.NewGuid().ToString("N");
        factory.Overrides["WorkerHealth:ProbeSeconds"] = "1";
        factory.Overrides["WorkerHealth:FailureGraceSeconds"] = "5";
        using var admin = await ClientAsync(factory, "Admin");
        using var customer = await ClientAsync(factory, "Customer");
        var monitor = factory.Services.GetRequiredService<WorkerProgress>();
        await UntilAsync(() => monitor.Snapshot().All(x => x.State == "Running"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/health/ready")).StatusCode);
        await factory.Services.GetRequiredService<BrokerQueueObserver>().CollectAsync(CancellationToken.None);
        Assert.True(factory.Services.GetRequiredService<MeterProvider>().ForceFlush());
        Assert.Contains("orderfleet.broker.ready_messages", metrics.Names);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/operations/runtime")).StatusCode);
        var diagnostic = await admin.GetStringAsync("/api/v1/operations/runtime");
        Assert.Contains("financial", diagnostic); Assert.Contains("lastSuccessUtc", diagnostic);
        Assert.DoesNotContain("amqp", diagnostic, StringComparison.OrdinalIgnoreCase);
        var last = monitor.Snapshot().Single(x => x.Worker == "financial").LastSuccessUtc;
        await UntilAsync(() => monitor.Snapshot().Single(x => x.Worker == "financial").LastSuccessUtc > last);
        await rabbit.StopAsync();
        try
        {
            await UntilAsync(() => monitor.Snapshot().Single(x => x.Worker == "financial").State == "Failed");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.GetAsync("/health/ready")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/health/live")).StatusCode);
        }
        finally
        {
            await rabbit.StartAsync();
            factory.Services.GetRequiredService<IOptions<RabbitOptions>>().Value.Uri = rabbit.ConnectionString;
        }
        await UntilAsync(() => monitor.Snapshot().All(x => x.State == "Running"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/health/ready")).StatusCode);
    }
    private static async Task UntilAsync(Func<bool> complete)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (!complete()) await Task.Delay(100, timeout.Token);
    }
}
