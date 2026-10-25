using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFleet.Application.Recovery;

namespace OrderFleet.Infrastructure.Recovery;

public sealed class RecoveryOptions
{
    public bool Enabled { get; set; } = true;
    public int PollMilliseconds { get; set; } = 1000;
    public int BatchSize { get; set; } = 20;
    public int OrphanGraceMinutes { get; set; } = 5;
    public int MaxFailureAttempts { get; set; } = 10;
}

public sealed class RecoveryWorker(IServiceScopeFactory scopes, IOptions<RecoveryOptions> options,
    ILogger<RecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        var failureCount = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<RecoverOrderFleet>().ExecuteAsync(stoppingToken);
                failureCount = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { failureCount++; logger.LogWarning("Recovery sweep failed {FailureType}", error.GetType().Name); }
            var delay = failureCount == 0 ? options.Value.PollMilliseconds : Math.Min(30000, 1000 * Math.Pow(2, Math.Min(5, failureCount)));
            await Task.Delay(TimeSpan.FromMilliseconds(delay), stoppingToken);
        }
    }
}
