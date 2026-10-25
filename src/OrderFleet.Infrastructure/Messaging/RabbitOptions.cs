namespace OrderFleet.Infrastructure.Messaging;

public sealed class RabbitOptions
{
    public bool Enabled { get; set; } = true;
    public string Uri { get; set; } = "";
    public string Prefix { get; set; } = "orderfleet";
    public int PublishTimeoutSeconds { get; set; } = 5;
    public int OutboxMaxAttempts { get; set; } = 10;
    public int PollMilliseconds { get; set; } = 500;
    public int LeaseSeconds { get; set; } = 60;
}
