using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OrderFleet.Application.Diagnostics;

public static class OrderFleetDiagnostics
{
    public const string Name = "OrderFleet";
    public static readonly ActivitySource Activities = new(Name, "1.0.0");
    public static readonly Meter Meter = new(Name, "1.0.0");
    public static string? CurrentTraceParent => Activity.Current?.IdFormat == ActivityIdFormat.W3C ? Activity.Current.Id : null;
    public static readonly Counter<long> HttpRequests = Meter.CreateCounter<long>("orderfleet.http.requests");
    public static readonly Histogram<double> HttpDuration = Meter.CreateHistogram<double>("orderfleet.http.duration", "ms");
    public static readonly Counter<long> Errors = Meter.CreateCounter<long>("orderfleet.errors");
    public static readonly Counter<long> OutboxPublished = Meter.CreateCounter<long>("orderfleet.outbox.published");
    public static readonly Counter<long> OutboxRetries = Meter.CreateCounter<long>("orderfleet.outbox.retries");
    public static readonly Histogram<double> OutboxLag = Meter.CreateHistogram<double>("orderfleet.outbox.delivery_lag", "s");
    public static readonly Counter<long> DispatchResults = Meter.CreateCounter<long>("orderfleet.dispatch.results");
    public static readonly Histogram<double> DispatchDuration = Meter.CreateHistogram<double>("orderfleet.dispatch.duration", "ms");
    public static readonly Counter<long> ConsumerProcessed = Meter.CreateCounter<long>("orderfleet.consumer.processed");
    public static readonly Counter<long> ConsumerRetries = Meter.CreateCounter<long>("orderfleet.consumer.retries");
    public static readonly Counter<long> ConsumerDeadLetters = Meter.CreateCounter<long>("orderfleet.consumer.dead_letters");
}
