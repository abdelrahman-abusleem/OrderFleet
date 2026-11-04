using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace OrderFleet.Api.Diagnostics;

public sealed class SafeLogPolicy : ILogEventEnricher
{
    private static readonly HashSet<string> Templates =
    [
        "Recovery sweep failed {FailureType}", "Cleanup sweep failed {FailureType}", "Admin {ActorId} cancelled order {OrderId}",
        "Admin {ActorId} read delivery for order {OrderId}", "Admin {ActorId} read order {OrderId}",
        "HTTP {RequestMethod} {RoutePattern} returned {StatusCode} in {ElapsedMilliseconds}ms",
        "Unhandled request failure {FailureType}; trace {TraceId}", "Request failed with {FailureType}; trace {TraceId}",
        "Outbox lease expired for {MessageId}", "Outbox publication failed {MessageId} {FailureCode} attempt {Attempt}",
        "Outbox completion deferred {MessageId} {FailureType}",
        "Courier capacity missing for order {OrderId}",
        "Outbox worker unavailable {FailureType}", "{Consumer} consumer unavailable {FailureType}",
        "Operational metrics unavailable {FailureType}",
        "Consumer {Consumer} failed {MessageId} {FailureCode} attempt {Attempt}"
    ];
    private static readonly HashSet<string> Properties =
    [
        "SourceContext", "TraceId", "SpanId", "ActorId", "OrderId", "MessageId", "FailureType", "FailureCode", "Attempt",
        "Consumer", "RequestMethod", "RoutePattern", "StatusCode", "ElapsedMilliseconds", "FailureCategory", "SourceCategory", "EventCode"
    ];
    public bool IsApproved(LogEvent logEvent)
    {


        if (!Templates.Contains(logEvent.MessageTemplate.Text)) return false;
        if (!logEvent.Properties.TryGetValue("SourceContext", out var property) || property is not ScalarValue { Value: string source }) return false;
        return source.StartsWith("OrderFleet", StringComparison.Ordinal) || source == "OrderAccessAudit" || source == "Microsoft.Hosting.Lifetime";
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        foreach (var key in logEvent.Properties.Keys.Where(key => !Properties.Contains(key)).ToArray()) logEvent.RemovePropertyIfPresent(key);
        if (Activity.Current is { } activity)
        {
            logEvent.AddPropertyIfAbsent(factory.CreateProperty("TraceId", activity.TraceId.ToString()));
            logEvent.AddPropertyIfAbsent(factory.CreateProperty("SpanId", activity.SpanId.ToString()));
        }
    }
}
