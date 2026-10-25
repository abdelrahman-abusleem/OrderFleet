using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace OrderFleet.IntegrationTests;

internal sealed class ApiFactory(PostgresFixture postgres) : WebApplicationFactory<Program>
{
    internal string SigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    internal const string Issuer = "https://orderfleet.test";
    internal const string Audience = "orderfleet-tests";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:OrderFleet", postgres.ConnectionString);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:OrderFleet"] = postgres.ConnectionString,
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience,
                ["Jwt:SigningKey"] = SigningKey,
                ["Jwt:LifetimeMinutes"] = "15"
            }));
    }
}
