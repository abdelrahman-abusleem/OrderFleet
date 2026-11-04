namespace OrderFleet.Api.Configuration;

public sealed class DemoHostOptions
{
    public bool Enabled { get; set; }
}

public static class DeploymentRegistration
{
    public static void AddDeploymentSafety(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var local = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        services.AddOptions<DemoHostOptions>().Bind(configuration.GetSection("Demo"))
            .Validate(options => local || options.Enabled, "The mock payment host requires explicit Demo:Enabled outside Development/Testing.")
            .Validate(_ => local || ValidHosts(configuration["AllowedHosts"]), "Explicit deployment host names are required.")
            .Validate(_ => local || Uri.TryCreate(configuration["Jwt:Issuer"], UriKind.Absolute, out var issuer)
                && issuer.Scheme == Uri.UriSchemeHttps, "Deployed JWT issuer must use HTTPS.")
            .ValidateOnStart();
        services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(180));
        services.AddHttpsRedirection(options => options.HttpsPort = 443);
    }

    private static bool ValidHosts(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Split(';', StringSplitOptions.TrimEntries).All(host => !host.Contains('*')
            && Uri.CheckHostName(host) != UriHostNameType.Unknown);
}
