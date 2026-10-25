using Microsoft.Extensions.Options;
using OrderFleet.Application.Couriers;
using OrderFleet.Application.Eta;
using OrderFleet.Infrastructure.Persistence;

namespace OrderFleet.Api.Configuration;

public sealed class EtaOptions
{
    public double AverageSpeedKmh { get; set; } = 25;
    public double DistanceFactor { get; set; } = 1.3;
    public int PreparationMinutes { get; set; } = 10;
    public int HandoffMinutes { get; set; } = 2;
    public int MaximumMinutes { get; set; } = 120;
}

public static class EtaRegistration
{
    public static void AddEta(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EtaOptions>().Bind(configuration.GetSection("Eta"))
            .Validate(x => double.IsFinite(x.AverageSpeedKmh) && x.AverageSpeedKmh is >= 5 and <= 100
                && double.IsFinite(x.DistanceFactor) && x.DistanceFactor is >= 1 and <= 3
                && x.PreparationMinutes is >= 0 and <= 60 && x.HandoffMinutes is >= 0 and <= 15
                && x.MaximumMinutes is >= 30 and <= 240, "Invalid ETA assumptions.").ValidateOnStart();
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EtaOptions>>().Value;
            return new EtaCalculator(new(options.AverageSpeedKmh, options.DistanceFactor, options.PreparationMinutes,
                options.HandoffMinutes, options.MaximumMinutes), sp.GetRequiredService<CourierPolicy>().LocationFreshness);
        });
        services.AddScoped<IEtaStore, EfEtaStore>();
        services.AddScoped<GetEta>();
    }
}
