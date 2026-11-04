using System.Net;
using Microsoft.Extensions.Options;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class ProductionConfigurationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task NonDevelopmentHost_RequiresExplicitDemoAcknowledgement()
    {
        await using var factory = new ApiFactory(fixture) { EnvironmentName = "Production" };
        factory.Overrides["AllowedHosts"] = "api.example.test";
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData("AllowedHosts", "*")]
    [InlineData("AllowedHosts", "*.example.test")]
    [InlineData("AllowedHosts", "")]
    [InlineData("Jwt:Issuer", "http://issuer.example.test")]
    public async Task ExplicitDemo_StillRejectsUnsafeDeploymentSettings(string key, string value)
    {
        await using var factory = new ApiFactory(fixture) { EnvironmentName = "Production" };
        factory.Overrides["Demo:Enabled"] = "true";
        factory.Overrides["AllowedHosts"] = "api.example.test";
        factory.Overrides[key] = value;
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task ExplicitDemo_IsLabeledAndUsesHsts_AndRejectsForeignHost()
    {
        await using var factory = new ApiFactory(fixture) { EnvironmentName = "Production" };
        factory.Overrides["Demo:Enabled"] = "true";
        factory.Overrides["AllowedHosts"] = "api.example.test";
        using var client = factory.CreateClient(new() { BaseAddress = new("https://api.example.test") });
        var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("mock", Assert.Single(response.Headers.GetValues("X-OrderFleet-Payments")));
        Assert.Contains("max-age=", Assert.Single(response.Headers.GetValues("Strict-Transport-Security")));
        using var foreign = factory.CreateClient(new() { BaseAddress = new("https://foreign.example.test") });
        Assert.Equal(HttpStatusCode.BadRequest, (await foreign.GetAsync("/health/live")).StatusCode);
    }
}
