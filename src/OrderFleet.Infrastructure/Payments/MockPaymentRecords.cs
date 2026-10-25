using OrderFleet.Application.Payments;

namespace OrderFleet.Infrastructure.Payments;

public sealed class MockPaymentAuthorization
{
    public Guid Id { get; private set; }
    public Guid ActorId { get; private set; }
    public string Key { get; private set; } = "";
    public string Fingerprint { get; private set; } = "";
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "SAR";
    public ProviderPaymentState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CapturedAt { get; private set; }
    public DateTimeOffset? SettledAt { get; private set; }

    private MockPaymentAuthorization() { }
    public MockPaymentAuthorization(Guid actorId, string key, string fingerprint, decimal amount, string currency,
        bool approved, DateTimeOffset now)
    {
        Id = Guid.NewGuid(); ActorId = actorId; Key = key; Fingerprint = fingerprint;
        Amount = amount; Currency = currency; CreatedAt = now.ToUniversalTime();
        State = approved ? ProviderPaymentState.Authorized : ProviderPaymentState.Declined;
    }
    public void Capture(DateTimeOffset now)
    {
        if (State != ProviderPaymentState.Authorized) throw new InvalidOperationException("Mock authorization cannot capture.");
        State = ProviderPaymentState.Captured; CapturedAt = now.ToUniversalTime();
    }
    public void Settle(DateTimeOffset now)
    {
        State = State switch
        {
            ProviderPaymentState.Authorized => ProviderPaymentState.Voided,
            ProviderPaymentState.Captured => ProviderPaymentState.Refunded,
            _ => throw new InvalidOperationException("Mock authorization cannot settle.")
        };
        SettledAt = now.ToUniversalTime();
    }
}

public sealed class MockPaymentEffect
{
    public Guid Id { get; private set; }
    public Guid AuthorizationId { get; private set; }
    public string Operation { get; private set; } = "";
    public bool Succeeded { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    private MockPaymentEffect() { }
    public MockPaymentEffect(Guid authorizationId, string operation, bool succeeded, DateTimeOffset now)
    {
        Id = Guid.NewGuid(); AuthorizationId = authorizationId; Operation = operation;
        Succeeded = succeeded; OccurredAt = now.ToUniversalTime();
    }
}

public sealed class MockPaymentOptions
{
    public string AuthorizeOutcome { get; set; } = "Success";
    public string CaptureOutcome { get; set; } = "Success";
}
