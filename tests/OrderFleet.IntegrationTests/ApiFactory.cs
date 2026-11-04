using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OrderFleet.IntegrationTests;

internal sealed class ApiFactory(PostgresFixture postgres, TimeProvider? clock = null, string? signingKey = null) : WebApplicationFactory<Program>
{
    internal string SigningKey { get; } = signingKey ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    internal Dictionary<string, string?> Overrides { get; } = new();
    internal Action<IServiceCollection>? CustomizeServices { get; set; }
    internal string EnvironmentName { get; set; } = "Testing";
    internal const string Issuer = "https://orderfleet.test";
    internal const string Audience = "orderfleet-tests";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.ConfigureServices(services => CustomizeServices?.Invoke(services));
        if (clock is not null)
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
            });
        builder.UseSetting("ConnectionStrings:OrderFleet", postgres.ConnectionString);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:OrderFleet"] = postgres.ConnectionString,
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience,
                ["Jwt:SigningKey"] = SigningKey,
                ["Jwt:LifetimeMinutes"] = "15",
                ["Messaging:Enabled"] = "false",
                ["Recovery:Enabled"] = "false",
                ["Cleanup:Enabled"] = "false",
                ["Telemetry:Exporter"] = "None",
                ["Telemetry:TraceSamplingRatio"] = "1"
            }).AddInMemoryCollection(Overrides));
    }
}
