using Microsoft.Extensions.Options;

namespace OrderFleet.Api.Configuration;

public sealed class FrontendCorsOptions
{
    public string[] AllowedOrigins { get; set; } = [];

    public bool IsValid(bool local) => AllowedOrigins is not null && AllowedOrigins.Length <= 20 && AllowedOrigins.All(origin =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        && (local || uri.Scheme == "https") && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && !origin.Contains('*') && origin == uri.GetLeftPart(UriPartial.Authority));
}

public static class CorsRegistration
{
    public static void AddFrontendCors(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<FrontendCorsOptions>().Bind(configuration.GetSection("Cors"))
            .Validate(options => options.IsValid(environment.IsDevelopment() || environment.IsEnvironment("Testing")), "Invalid frontend origins.")
            .ValidateOnStart();
        services.AddCors();
        services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>().Configure<IOptions<FrontendCorsOptions>>((cors, configured) =>
            cors.AddDefaultPolicy(policy =>
            {
                if (configured.Value.AllowedOrigins.Length > 0)
                    policy.WithOrigins(configured.Value.AllowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
            }));
    }

    public static void UseHubOriginBoundary(this WebApplication app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/hubs/orders") && context.Request.Headers.TryGetValue("Origin", out var origins))
        {
            var allowed = context.RequestServices.GetRequiredService<IOptions<FrontendCorsOptions>>().Value.AllowedOrigins;
            var ownOrigin = context.Request.Scheme + "://" + context.Request.Host.Value;
            if (origins.Count != 1 || !string.Equals(origins[0], ownOrigin, StringComparison.OrdinalIgnoreCase)
                && !allowed.Contains(origins[0], StringComparer.OrdinalIgnoreCase))
            { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        }
        await next(context);
    });
}
