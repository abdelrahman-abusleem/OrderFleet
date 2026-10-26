using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFleet.Application.Recovery;
using OrderFleet.Infrastructure.Operations;

namespace OrderFleet.Infrastructure.Recovery;

public sealed class RecoveryOptions
{
    public bool Enabled { get; set; } = true;
    public int PollMilliseconds { get; set; } = 1000;
    public int MaxIdlePollMilliseconds { get; set; } = 10000;
    public int BatchSize { get; set; } = 20;
    public int OrphanGraceMinutes { get; set; } = 5;
    public int MaxFailureAttempts { get; set; } = 10;

    public int PollDelayMilliseconds(int emptySweeps) => (int)Math.Min(MaxIdlePollMilliseconds,
        PollMilliseconds * Math.Pow(2, Math.Clamp(emptySweeps, 0, 16)));
}

public sealed class RecoveryWorker(IServiceScopeFactory scopes, IOptions<RecoveryOptions> options,
    ILogger<RecoveryWorker> logger, WorkerProgress progress) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) { progress.Disable(WorkerNames.Recovery); return; }
        progress.Begin(WorkerNames.Recovery);
        var failureCount = 0;
        var emptySweeps = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    progress.BeginWork(WorkerNames.Recovery);
                    var examined = await scope.ServiceProvider.GetRequiredService<RecoverOrderFleet>().ExecuteAsync(stoppingToken);
                    emptySweeps = examined == 0 ? Math.Min(16, emptySweeps + 1) : 0;
                    progress.Success(WorkerNames.Recovery);
                    failureCount = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) { progress.Failure(WorkerNames.Recovery); failureCount++; logger.LogWarning("Recovery sweep failed {FailureType}", error.GetType().Name); }
                var delay = failureCount == 0 ? options.Value.PollDelayMilliseconds(emptySweeps) : Math.Min(30000, 1000 * Math.Pow(2, Math.Min(5, failureCount)));
                await Task.Delay(TimeSpan.FromMilliseconds(delay), stoppingToken);
            }
        }
        finally { progress.Stop(WorkerNames.Recovery); }
    }
}
