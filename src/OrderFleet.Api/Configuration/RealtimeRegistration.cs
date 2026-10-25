using OrderFleet.Api.Realtime;
using OrderFleet.Application.Realtime;
using OrderFleet.Infrastructure.Persistence;

namespace OrderFleet.Api.Configuration;

public static class RealtimeRegistration
{
    public static void AddOrderRealtime(this IServiceCollection services)
    {
        services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = false;
            options.MaximumReceiveMessageSize = 8192;
            options.MaximumParallelInvocationsPerClient = 1;
        });
        services.AddSingleton<OrderConnections>();
        services.AddSingleton<OrderRealtimeNotifier>();
        services.AddSingleton<IOrderNotifier>(sp => sp.GetRequiredService<OrderRealtimeNotifier>());
        services.AddScoped<IRealtimeStore, EfRealtimeStore>();
        services.AddScoped<GetRealtimeOrder>();
        services.AddScoped<RealtimeEventHandler>();
    }
}
