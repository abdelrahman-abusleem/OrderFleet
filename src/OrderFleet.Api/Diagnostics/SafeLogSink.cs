using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace OrderFleet.Api.Diagnostics;



public sealed class SafeLogSink(Serilog.Core.Logger output) : ILogEventSink, IDisposable
{
    private readonly SafeLogPolicy policy = new();
    private static readonly MessageTemplate Incident = new MessageTemplateParser().Parse(
        "Operational incident {FailureCategory} {FailureType} in {SourceCategory} ({EventCode})");

    public void Emit(LogEvent original)
    {
        LogEvent safe;
        if (policy.IsApproved(original))
            safe = new(original.Timestamp, original.Level, null, original.MessageTemplate,
                original.Properties.Select(x => new LogEventProperty(x.Key, x.Key == "SourceContext"
                    ? new ScalarValue("OrderFleet.Approved") : x.Value)));
        else
        {
            if (original.Level < LogEventLevel.Warning) return;
            var source = original.Properties.GetValueOrDefault("SourceContext") is ScalarValue { Value: string name } ? name : "";
            var category = source.StartsWith("Npgsql", StringComparison.Ordinal) || source.Contains("EntityFrameworkCore", StringComparison.Ordinal) ? "Database"
                : source.Contains("Messaging", StringComparison.Ordinal) || source.StartsWith("RabbitMQ", StringComparison.Ordinal) ? "Messaging"
                : source.StartsWith("OrderFleet", StringComparison.Ordinal) ? "Application"
                : source.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal) ? "Hosting" : "Framework";
            var eventCode = original.Properties.GetValueOrDefault("EventId") switch
            {
                ScalarValue { Value: int id } => id,
                StructureValue structure when structure.Properties.FirstOrDefault(x => x.Name == "Id")?.Value is ScalarValue { Value: int id } => id,
                _ => 0
            };
            var type = original.Exception?.GetType().Name ?? (category == "Database" && eventCode == 20102 ? "DatabaseCommandFailure" : "Unclassified");
            if (type.Length > 100 || type.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '`'))) type = "Exception";
            var failure = category == "Database" ? "DatabaseFailure" : original.Exception is TimeoutException or IOException ? "DependencyFailure" : "Unexpected";
            safe = new(original.Timestamp, original.Level, null, Incident,
                [new("FailureCategory", new ScalarValue(failure)), new("FailureType", new ScalarValue(type)),
                 new("SourceCategory", new ScalarValue(category)), new("EventCode", new ScalarValue(eventCode)),
                 new("SourceContext", new ScalarValue("OrderFleet.SafeIncident"))]);
            Activity.Current?.SetStatus(ActivityStatusCode.Error);
            Activity.Current?.SetTag("error.type", failure);
            OrderFleet.Application.Diagnostics.OrderFleetDiagnostics.Errors.Add(1,
                new KeyValuePair<string, object?>("component", category));
        }
        policy.Enrich(safe, new PropertyFactory());
        output.Write(safe);
    }
    public void Dispose() => output.Dispose();
    private sealed class PropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) => new(name, new ScalarValue(value));
    }
}
