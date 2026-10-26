using System.Text.Json;
using System.Text.Json.Serialization;
using OrderFleet.Application.Messaging;
using OrderFleet.Domain.Common;

namespace OrderFleet.Application.Payments;

public sealed record FinancialFact(Guid PaymentId, Guid OrderId, Guid AuthorizationId, decimal Amount, string Currency, string State);
public interface IFinancialArchive
{
    Task StoreAsync(IntegrationEvent envelope, FinancialFact fact, CancellationToken ct);
}

// An immutable history projection, never an instruction to charge or mutate Payment.
public sealed class ArchiveFinancialEvent(IFinancialArchive archive) : IIntegrationEventHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 4 };

    public Task HandleAsync(IntegrationEvent message, CancellationToken cancellationToken)
    {
        var state = message.Type switch
        {
            IntegrationEventTypes.PaymentAuthorized => "Authorized",
            IntegrationEventTypes.PaymentCaptured => "Captured",
            IntegrationEventTypes.PaymentFailed => "Failed",
            IntegrationEventTypes.PaymentCaptureUncertain => "PendingReconciliation",
            IntegrationEventTypes.PaymentVoided => "Voided",
            IntegrationEventTypes.PaymentRefunded => "Refunded",
            _ => throw new InvalidDataException("Unsupported financial fact.")
        };
        if (message.Version != 1 || message.MessageId == Guid.Empty || message.OccurredAtUtc == default
            || message.OccurredAtUtc.Offset != TimeSpan.Zero) throw new InvalidDataException("Invalid financial envelope.");
        var fact = JsonSerializer.Deserialize<FinancialFact>(message.PayloadJson, Json);
        if (fact is null || fact.PaymentId == Guid.Empty || fact.AuthorizationId == Guid.Empty || fact.OrderId == Guid.Empty
            || fact.OrderId != message.AggregateId || fact.Amount <= 0 || decimal.Round(fact.Amount, 2) != fact.Amount
            || fact.Currency != CurrencyCodes.Sar || fact.State != state) throw new InvalidDataException("Invalid financial fact.");
        return archive.StoreAsync(message, fact, cancellationToken);
    }
}
