using OrderFleet.Application.Messaging;
using OrderFleet.Application.Payments;

namespace OrderFleet.Infrastructure.Payments;

public sealed class FinancialAuditRecord
{
    public Guid MessageId { get; private set; }
    public string Type { get; private set; } = "";
    public int Version { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public Guid PaymentId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid AuthorizationId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "";
    public string State { get; private set; } = "";
    public DateTimeOffset ArchivedAtUtc { get; private set; }
    private FinancialAuditRecord() { }
    public bool Matches(IntegrationEvent message, FinancialFact fact) => Type == message.Type && Version == message.Version
        && OccurredAtUtc.Ticks == message.OccurredAtUtc.Ticks - message.OccurredAtUtc.Ticks % 10 && PaymentId == fact.PaymentId && OrderId == fact.OrderId
        && AuthorizationId == fact.AuthorizationId && Amount == fact.Amount && Currency == fact.Currency && State == fact.State;
}
