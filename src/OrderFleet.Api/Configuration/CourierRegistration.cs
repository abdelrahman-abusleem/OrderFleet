using Microsoft.Extensions.Options;
using OrderFleet.Application.Couriers;
using OrderFleet.Infrastructure.Persistence;

namespace OrderFleet.Api.Configuration;

public sealed class CourierOptions
{
    public int LocationFreshnessSeconds { get; set; } = 120;
    public int FutureSkewSeconds { get; set; } = 15;
    public double MaxRadiusMeters { get; set; } = 20000;
}

public static class CourierRegistration
{
    public static void AddCouriers(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CourierOptions>().Bind(configuration.GetSection("Couriers"))
            .Validate(x => x.LocationFreshnessSeconds is >= 10 and <= 600 && x.FutureSkewSeconds is >= 0 and <= 30
                && double.IsFinite(x.MaxRadiusMeters) && x.MaxRadiusMeters is >= 100 and <= 50000,
                "Invalid courier freshness, skew or radius policy.").ValidateOnStart();
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<CourierOptions>>().Value;
            return new CourierPolicy(TimeSpan.FromSeconds(options.LocationFreshnessSeconds),
                TimeSpan.FromSeconds(options.FutureSkewSeconds), options.MaxRadiusMeters);
        });
        services.AddScoped<ICourierStore, EfCourierStore>();
        services.AddScoped<ManageCourier>();
        services.AddScoped<FindNearbyCouriers>();
    }
}
