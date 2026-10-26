using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OrderFleet.Infrastructure.Messaging;
using OrderFleet.Infrastructure.Operations;
using OrderFleet.Infrastructure.Recovery;

namespace OrderFleet.IntegrationTests;

public sealed class WorkerProgressTests
{
    private readonly ProgressClock clock = new();
    private WorkerProgress Monitor() => new(clock, Options.Create(new WorkerHealthOptions { StartupGraceSeconds = 15, FailureGraceSeconds = 5 }));
    [Fact]
    public void Disabled_IsExplicitlyNotReady_EvenDuringStartupGrace()
    {
        var monitor = Monitor(); monitor.Disable("outbox");
        var state = monitor.Snapshot().Single(x => x.Worker == "outbox");
        Assert.Equal("Disabled", state.State); Assert.False(state.Ready); Assert.Null(state.LastSuccessUtc);
    }
    [Fact]
    public void Starting_HasBoundedGrace_ThenRequiresProgress()
    {
        var monitor = Monitor();
        Assert.All(monitor.Snapshot(), x => Assert.True(x.Ready));
        clock.Advance(16);
        Assert.All(monitor.Snapshot(), x => { Assert.False(x.Ready); Assert.Equal("Starting", x.State); });
    }
    [Fact]
    public void VerifiedIdleProbes_KeepAnEmptyConsumerHealthy()
    {
        var monitor = Monitor();
        for (var count = 0; count < 100; count++) { clock.Advance(5); monitor.Success("financial", idleProbe: true); }
        var state = monitor.Snapshot().Single(x => x.Worker == "financial");
        Assert.True(state.Ready); Assert.Equal("Running", state.State); Assert.Equal(clock.GetUtcNow(), state.LastSuccessUtc);
    }
    [Fact]
    public void IdleSocketProbes_CannotHideAStuckInFlightHandler()
    {
        var monitor = Monitor();
        monitor.Success("financial"); monitor.BeginWork("financial"); clock.Advance(91);
        monitor.Success("financial", idleProbe: true);
        var state = monitor.Snapshot().Single(x => x.Worker == "financial");
        Assert.False(state.Ready); Assert.Equal("Stalled", state.State);
    }
    [Fact]
    public void PersistentFailure_DoesNotRenewItsGraceOnEveryRetry_AndCanRecover()
    {
        var monitor = Monitor(); monitor.Success("recovery"); monitor.Failure("recovery");
        Assert.True(monitor.Snapshot().Single(x => x.Worker == "recovery").Ready);
        clock.Advance(6); monitor.BeginWork("recovery"); monitor.Failure("recovery");
        Assert.False(monitor.Snapshot().Single(x => x.Worker == "recovery").Ready);
        monitor.Success("recovery"); Assert.True(monitor.Snapshot().Single(x => x.Worker == "recovery").Ready);
    }
    [Fact]
    public async Task EnabledFlags_DoNotConcealWorkerTermination_AndRestartCanRecover()
    {
        var monitor = Monitor(); monitor.Stop("outbox");
        var health = new WorkersHealthCheck(Options.Create(new RabbitOptions()), Options.Create(new RecoveryOptions()), monitor);
        Assert.Equal(HealthStatus.Unhealthy, (await health.CheckHealthAsync(new())).Status);
        monitor.Begin("outbox"); monitor.Success("outbox");
        Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(new())).Status);
    }
    [Fact]
    public void WallClockChange_DoesNotRenewMonotonicProgress()
    {
        var monitor = Monitor(); monitor.Success("outbox"); clock.Advance(91); clock.UtcAdjustment = TimeSpan.FromHours(-3);
        Assert.Equal("Stalled", monitor.Snapshot().Single(x => x.Worker == "outbox").State);
    }
    private sealed class ProgressClock : TimeProvider
    {
        private long seconds;
        public TimeSpan UtcAdjustment { get; set; }
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => seconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(seconds) + UtcAdjustment;
        public void Advance(int value) => seconds += value;
    }
}
