using Microsoft.Extensions.Options;
using OrderFleet.Application.Catalog;
using OrderFleet.Application.Clock;
using OrderFleet.Application.Orders;
using OrderFleet.Domain.Orders;
using OrderFleet.Infrastructure.Clock;
using OrderFleet.Infrastructure.Persistence;
using OrderFleet.Application.Payments;
using OrderFleet.Infrastructure.Payments;

namespace OrderFleet.Api.Configuration;

public sealed class OrderingOptions
{
    public int AcceptanceTimeoutMinutes { get; set; } = 5;
    public int MaxQuantityPerItem { get; set; } = 99;
    public int MaxItemsPerOrder { get; set; } = 20;
    public decimal MaxUnitPrice { get; set; } = 9999.99m;
    public decimal MaxOrderTotal { get; set; } = 99999.99m;
    public OrderPricingLimits BuildLimits() => new(MaxQuantityPerItem, MaxItemsPerOrder, MaxUnitPrice, MaxOrderTotal);
}

public static class OrderingRegistration
{
    public static void AddOrdering(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OrderingOptions>().Bind(configuration.GetSection("Ordering"))
            .Validate(x => x.AcceptanceTimeoutMinutes is >= 1 and <= 60, "Acceptance timeout must be 1..60 minutes.")
            .Validate(x => { try { _ = x.BuildLimits(); return true; } catch (ArgumentException) { return false; } },
                "Ordering monetary limits are invalid.").ValidateOnStart();
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<OrderingOptions>>().Value;
            return new OrderingPolicy(options.BuildLimits(), TimeSpan.FromMinutes(options.AcceptanceTimeoutMinutes));
        });
        services.AddScoped<ICatalogStore, EfCatalogStore>();
        services.AddScoped<IRestaurantAccess, EfCatalogStore>();
        services.AddScoped<EfOrderingStore>();
        services.AddScoped<IOrderCreationStore>(sp => sp.GetRequiredService<EfOrderingStore>());
        services.AddScoped<IOrderTransitionStore>(sp => sp.GetRequiredService<EfOrderingStore>());
        services.AddScoped<IOrderQueryStore>(sp => sp.GetRequiredService<EfOrderingStore>());
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<ManageCatalog>();
        services.AddScoped<BrowseCatalog>();
        services.AddScoped<CreateOrder>();
        services.AddScoped<OrderCommands>();
        services.AddScoped<QueryOrders>();
        services.AddOptions<MockPaymentOptions>().Bind(configuration.GetSection("MockPayments"))
            .Validate(x => x.AuthorizeOutcome is "Success" or "Declined" or "Transient", "Invalid mock authorization outcome.")
            .Validate(x => x.CaptureOutcome is "Success" or "Declined" or "Transient" or "Unknown", "Invalid mock capture outcome.")
            .ValidateOnStart();
        services.AddScoped<DurableMockPaymentProvider>();
        services.AddScoped<IPaymentProvider>(provider => provider.GetRequiredService<DurableMockPaymentProvider>());
        services.AddScoped<IPaymentStore, EfPaymentStore>();
        services.AddScoped<SettleCancelledPayment>();
        services.AddScoped<CaptureOrderPayment>();
    }
}
