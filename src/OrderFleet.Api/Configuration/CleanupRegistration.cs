using OrderFleet.Infrastructure.Maintenance;

namespace OrderFleet.Api.Configuration;

public static class CleanupRegistration
{
    public static void AddDataCleanup(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CleanupOptions>().Bind(configuration.GetSection("Cleanup"))
            .Validate(x => x.BatchSize is >= 1 and <= 1000 && x.PollSeconds is >= 1 and <= 86400
                && x.PublishedOutboxDays is >= 1 and <= 3650 && x.ResolvedRecoveryDays is >= 1 and <= 3650
                && x.FinishedDispatchDays is >= 1 and <= 3650, "Invalid cleanup bounds.").ValidateOnStart();
        services.AddSingleton<ExpiredDataCleanup>();
        services.AddHostedService<CleanupWorker>();
    }
}
