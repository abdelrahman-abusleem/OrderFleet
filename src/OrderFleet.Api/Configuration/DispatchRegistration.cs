using Microsoft.Extensions.Options;
using OrderFleet.Application.Dispatch;
using OrderFleet.Application.Couriers;
using OrderFleet.Infrastructure.Persistence;

namespace OrderFleet.Api.Configuration;

public sealed class DispatchOptions
{
    public double RadiusMeters { get; set; } = 5000;
    public int MaxAttempts { get; set; } = 10;
}

public static class DispatchRegistration
{
    public static void AddDispatch(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DispatchOptions>().Bind(configuration.GetSection("Dispatch"))
            .Validate(x => double.IsFinite(x.RadiusMeters) && x.RadiusMeters is >= 100 and <= 50000
                && x.MaxAttempts is >= 1 and <= 30, "Invalid dispatch radius or attempt limit.")
            .Validate<CourierPolicy>((x, couriers) => x.RadiusMeters <= couriers.MaxRadiusMeters,
                "Dispatch radius exceeds courier policy.").ValidateOnStart();
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DispatchOptions>>().Value;
            return new DispatchPolicy(options.RadiusMeters, options.MaxAttempts);
        });
        services.AddScoped<IDispatchStore, EfDispatchStore>();
        services.AddScoped<DispatchOrder>();
        services.AddScoped<DispatchEventHandler>();
        services.AddScoped<CourierOrderCommands>();
    }
}
