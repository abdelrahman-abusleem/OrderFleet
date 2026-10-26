using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OrderFleet.Application.Operations;
using OrderFleet.Infrastructure.Operations;
using OrderFleet.Infrastructure.Persistence;

namespace OrderFleet.Api.Configuration;

public static class OperationsRegistration
{
    public static void AddOperations(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WorkerHealthOptions>().Bind(configuration.GetSection("WorkerHealth"))
            .Validate(x => x.StartupGraceSeconds is >= 15 and <= 300 && x.StallSeconds is >= 60 and <= 600
                && x.FailureGraceSeconds is >= 5 and <= 120 && x.ProbeSeconds is >= 1 and <= 15, "Invalid worker health thresholds.").ValidateOnStart();
        services.AddSingleton<WorkerProgress>();
        services.AddSingleton<BrokerQueueObserver>();
        services.AddSingleton<IRuntimeDiagnostics, RuntimeDiagnostics>();
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
