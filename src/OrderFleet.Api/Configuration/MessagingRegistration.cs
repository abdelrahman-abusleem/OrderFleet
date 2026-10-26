using OrderFleet.Application.Messaging;
using OrderFleet.Application.Payments;
using OrderFleet.Infrastructure.Messaging;

namespace OrderFleet.Api.Configuration;

public static class MessagingRegistration
{
    public static void AddMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RabbitOptions>().Bind(configuration.GetSection("Messaging"))
            .Validate(x => !x.Enabled || (Uri.TryCreate(x.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "amqp" or "amqps"),
                "Messaging URI must be configured through secrets/environment.")
            .Validate(x => x.Prefix.Length is >= 1 and <= 50 && x.Prefix.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), "Invalid broker prefix.")
            .Validate(x => x.PublishTimeoutSeconds is >= 1 and <= 10 && x.LeaseSeconds >= x.PublishTimeoutSeconds * 5 + 10
                && x.LeaseSeconds <= 300 && x.OutboxMaxAttempts is >= 1 and <= 100 && x.PollMilliseconds is >= 100 and <= 5000,
                "Invalid messaging bounds or lease shorter than batch timeout.").ValidateOnStart();
        services.AddSingleton<IOutboxStore, PostgresOutboxStore>();
        services.AddSingleton<IEventPublisher, RabbitEventPublisher>();
        services.AddSingleton<OutboxRelay>();
        services.AddSingleton<RabbitDeliveryProcessor>();
        services.AddSingleton<RabbitConsumer>();
        services.AddScoped<CancelledPaymentEventHandler>();
        services.AddScoped<IFinancialArchive, OrderFleet.Infrastructure.Persistence.EfFinancialArchive>();
        services.AddScoped<ArchiveFinancialEvent>();
        services.AddHostedService<MessagingWorker>();
    }
}
