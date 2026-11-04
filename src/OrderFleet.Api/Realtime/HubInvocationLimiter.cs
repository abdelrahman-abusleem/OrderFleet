using System.Threading.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using OrderFleet.Api.Identity;

namespace OrderFleet.Api.Realtime;

public sealed class HubInvocationLimiter(IOptions<RequestLimitOptions> options) : IHubFilter, IDisposable
{
    private readonly PartitionedRateLimiter<string> limiter = PartitionedRateLimiter.Create<string, string>(actor =>
        RateLimitPartition.GetFixedWindowLimiter(actor, _ => new FixedWindowRateLimiterOptions
        { PermitLimit = options.Value.HubPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var actor = context.Context.User!.FindFirst("sub")!.Value;
        using var lease = await limiter.AcquireAsync(actor, 1, context.Context.ConnectionAborted);
        if (!lease.IsAcquired) throw new HubException("Too many requests.");
        return await next(context);
    }

    public void Dispose() => limiter.Dispose();
}
