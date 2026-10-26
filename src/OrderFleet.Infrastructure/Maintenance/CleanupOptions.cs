namespace OrderFleet.Infrastructure.Maintenance;

public sealed class CleanupOptions
{
    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 200;
    public int PollSeconds { get; set; } = 300;
    public int PublishedOutboxDays { get; set; } = 7;
    public int ResolvedRecoveryDays { get; set; } = 30;
    public int FinishedDispatchDays { get; set; } = 30;
}
