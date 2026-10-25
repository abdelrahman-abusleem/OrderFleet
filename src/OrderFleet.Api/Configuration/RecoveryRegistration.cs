using Microsoft.Extensions.Options;
using OrderFleet.Application.Payments;
using OrderFleet.Application.Recovery;
using OrderFleet.Infrastructure.Persistence;
using OrderFleet.Infrastructure.Recovery;

namespace OrderFleet.Api.Configuration;

public static class RecoveryRegistration
{
    public static void AddRecovery(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RecoveryOptions>().Bind(configuration.GetSection("Recovery"))
            .Validate(x => x.BatchSize is >= 1 and <= 100 && x.PollMilliseconds is >= 100 and <= 10000
                && x.OrphanGraceMinutes is >= 1 and <= 60, "Invalid recovery bounds.").ValidateOnStart();
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RecoveryOptions>>().Value;
            return new RecoveryPolicy(options.BatchSize, TimeSpan.FromMinutes(options.OrphanGraceMinutes));
        });
        services.AddScoped<IRecoveryStore, EfRecoveryStore>();
        services.AddScoped<RecoverOrderFleet>();
        services.AddScoped<ReconcilePaymentCapture>();
        services.AddHostedService<RecoveryWorker>();
    }
}
