using System.Diagnostics;
using OpenTelemetry;

namespace OrderFleet.Api.Diagnostics;

public sealed class SafeActivityProcessor : BaseProcessor<Activity>
{
    private static readonly HashSet<string> AllowedTags =
    [
        "http.request.method", "http.response.status_code", "http.route", "url.scheme", "network.protocol.version",
        "db.system", "db.system.name", "db.operation.name", "dispatch.outcome", "messaging.operation.type",
        "messaging.system", "messaging.message.type", "messaging.message.id", "error.type"
    ];

    public override void OnEnd(Activity activity)
    {
        activity.TraceStateString = null;
        for (var current = activity; current is not null; current = current.Parent)
            foreach (var key in current.Baggage.Select(x => x.Key).Distinct().ToArray()) current.SetBaggage(key, null);
        foreach (var fact in activity.Events)
        {
            // Fail closed for a library that bypasses the public exception recorder
            // and attaches unreviewed immutable event payloads directly.
            var redactedException = fact.Name == "exception" && fact.Tags.All(tag =>
                tag.Key is "exception.message" or "exception.stacktrace" ? Equals(tag.Value, "[redacted]")
                : tag.Key == "exception.type" && tag.Value is string type && type.Length <= 200
                    && type.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '_' or '`'));
            if (fact.Tags.Any() && !redactedException) activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
        foreach (var key in activity.TagObjects.Select(x => x.Key).Where(x => !AllowedTags.Contains(x)).ToArray())
            activity.SetTag(key, null);
        activity.SetStatus(activity.Status, null); // Exception descriptions may contain provider/customer data.
        if (activity.Source.Name == "Npgsql") activity.DisplayName = "postgresql.command";
        else if (activity.Source.Name == "Microsoft.AspNetCore") activity.DisplayName = "http.request";
    }
}
