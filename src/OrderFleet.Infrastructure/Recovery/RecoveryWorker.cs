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
}

public sealed class RecoveryWorker(IServiceScopeFactory scopes, IOptions<RecoveryOptions> options,
    ILogger<RecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<RecoverOrderFleet>().ExecuteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Recovery sweep failed {FailureType}", error.GetType().Name); }
            await Task.Delay(options.Value.PollMilliseconds, stoppingToken);
        }
    }
}
