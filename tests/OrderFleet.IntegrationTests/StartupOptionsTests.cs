using Microsoft.Extensions.Options;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class StartupOptionsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("Telemetry:Exporter", "Unknown")]
    [InlineData("Telemetry:MetricIntervalMilliseconds", "0")]
    [InlineData("Telemetry:TraceSamplingRatio", "2")]
    [InlineData("Telemetry:OtlpEndpoint", "http://user:private-marker@localhost:4317")]
    [InlineData("Dispatch:RadiusMeters", "50001")]
    [InlineData("Messaging:LeaseSeconds", "10")]
    [InlineData("Recovery:BatchSize", "0")]
    [InlineData("Eta:AverageSpeedKmh", "0")]
    public async Task InvalidCriticalOptions_FailStartupWithoutEchoingSecrets(string option, string value)
    {
        await using var factory = new ApiFactory(fixture);
        factory.Overrides[option] = value;
        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.DoesNotContain("private-marker", error.Message);
    }
}
