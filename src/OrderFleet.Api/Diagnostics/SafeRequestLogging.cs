using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using OrderFleet.Application.Diagnostics;

namespace OrderFleet.Api.Diagnostics;

public sealed class SafeRequestLogging(RequestDelegate next, ILogger<SafeRequestLogging> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try { await next(context); }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            var method = context.Request.Method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS" or "HEAD"
                ? context.Request.Method : "OTHER";
            var status = context.Response.StatusCode;
            var tags = new TagList { { "http.request.method", method }, { "http.route", route }, { "http.response.status_code", status } };
            OrderFleetDiagnostics.HttpRequests.Add(1, tags);
            OrderFleetDiagnostics.HttpDuration.Record(elapsed, tags);
            if (status >= 400) OrderFleetDiagnostics.Errors.Add(1, new TagList { { "component", "api" }, { "http.response.status_code", status } });
            logger.LogInformation("HTTP {RequestMethod} {RoutePattern} returned {StatusCode} in {ElapsedMilliseconds}ms",
                method, route, status, elapsed);
        }
    }
}
