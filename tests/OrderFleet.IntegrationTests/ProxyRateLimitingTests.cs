using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class ProxyRateLimitingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("192.0.2.1", false, true, true, false)]
    [InlineData("198.51.100.1", false, true, true, true)]
    [InlineData("192.0.2.1", false, true, false, true)]
    [InlineData("192.0.2.25", true, true, true, false)]
    [InlineData("192.0.2.1", false, false, true, true)]
    public async Task CredentialPartition_UsesForwardedClientOnlyFromConfiguredTrustedSource(
        string source, bool network, bool enabled, bool changingClient, bool limited)
    {
        await using var factory = new ApiFactory(fixture);
        factory.Overrides["TrustedProxy:Enabled"] = enabled.ToString();
        factory.Overrides[network ? "TrustedProxy:KnownNetworks:0" : "TrustedProxy:KnownProxies:0"] = network ? "192.0.2.0/24" : "192.0.2.1";
        factory.CustomizeServices = services => services.AddSingleton<IStartupFilter>(new RemoteAddress(IPAddress.Parse(source)));
        using var client = factory.CreateClient();
        for (var attempt = 1; attempt <= 11; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { }) };
            request.Headers.Add("X-Forwarded-For", "203.0.113." + (changingClient ? attempt : 1));
            using var response = await client.SendAsync(request);
            Assert.Equal(attempt == 11 && limited ? HttpStatusCode.TooManyRequests : HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("KnownProxies:0", "https://private-marker:password@proxy")]
    [InlineData("KnownNetworks:0", "0.0.0.0/0")]
    [InlineData("KnownNetworks:0", "::/0")]
    public async Task InvalidOrUnboundedTrust_FailsStartupWithoutEchoingConfiguration(string? key, string? value)
    {
        await using var factory = new ApiFactory(fixture);
        factory.Overrides["TrustedProxy:Enabled"] = "true";
        if (key is not null) factory.Overrides["TrustedProxy:" + key] = value;
        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.DoesNotContain("private-marker", error.Message);
        Assert.DoesNotContain("password", error.Message);
    }

    private sealed class RemoteAddress(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext context, RequestDelegate following) =>
            {
                context.Connection.RemoteIpAddress = address;
                return following(context);
            });
            next(app);
        };
    }
}
