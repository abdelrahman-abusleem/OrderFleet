namespace OrderFleet.Infrastructure.Messaging;

public sealed class OutboxOptions
{
    public int BatchSize { get; set; } = 5;
}
