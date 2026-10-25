using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OrderFleet.Application.Operations;
using OrderFleet.Infrastructure.Operations;
using OrderFleet.Infrastructure.Persistence;

namespace OrderFleet.Api.Configuration;

public static class OperationsRegistration
{
    public static void AddOperations(this IServiceCollection services)
    {
        services.AddScoped<IOperationsStore, EfOperationsStore>();
        services.AddScoped<OperateOrderFleet>();
        services.AddSingleton<OperationalMetrics>();
        services.AddSingleton<OperationalMetricSampler>();
        services.AddHostedService<OperationalMetricsWorker>();
        services.AddHealthChecks().AddCheck("process", () => HealthCheckResult.Healthy(), tags: ["live"])
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"], timeout: TimeSpan.FromSeconds(3))
            .AddCheck<BrokerHealthCheck>("broker", tags: ["ready"], timeout: TimeSpan.FromSeconds(3))
            .AddCheck<WorkersHealthCheck>("workers", tags: ["ready"]);
    }

    public static void MapHealth(this WebApplication app)
    {
        foreach (var mode in new[] { "live", "ready" })
            app.MapHealthChecks($"/health/{mode}", new HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains(mode),
                ResponseWriter = (context, report) => context.Response.WriteAsJsonAsync(new
                    { status = report.Status == HealthStatus.Healthy ? "Healthy" : "Unavailable" })
            }).AllowAnonymous().RequireRateLimiting("health");
    }
}
