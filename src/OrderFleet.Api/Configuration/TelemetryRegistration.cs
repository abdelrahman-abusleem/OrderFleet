using OrderFleet.Api.Diagnostics;
using OrderFleet.Application.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Npgsql;

namespace OrderFleet.Api.Configuration;

public sealed class TelemetryOptions
{
    public string Exporter { get; set; } = "Console";
    public string OtlpEndpoint { get; set; } = "http://localhost:4317";
    public int MetricIntervalMilliseconds { get; set; } = 10000;
    public double TraceSamplingRatio { get; set; } = 0.1;
}

public static class TelemetryRegistration
{
    public static void AddOrderTelemetry(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<SafeExceptionListener>();
        builder.Services.AddSerilog((services, configuration) =>
        {
            var output = new LoggerConfiguration().WriteTo.Console(new CompactJsonFormatter());
            foreach (var sink in services.GetServices<Serilog.Core.ILogEventSink>()) output.WriteTo.Sink(sink);
            configuration.MinimumLevel.Information().MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .WriteTo.Sink(new SafeLogSink(output.CreateLogger()));
        }, preserveStaticLogger: true);
        builder.Services.AddOptions<TelemetryOptions>().Bind(builder.Configuration.GetSection("Telemetry"))
            .Validate(x => x.Exporter is "Console" or "Otlp" or "None", "Invalid telemetry exporter.")
            .Validate(x => Uri.TryCreate(x.OtlpEndpoint, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query), "Invalid OTLP endpoint.")
            .Validate(x => x.MetricIntervalMilliseconds is >= 1000 and <= 60000, "Invalid metric export interval.")
            .Validate(x => double.IsFinite(x.TraceSamplingRatio) && x.TraceSamplingRatio is >= 0 and <= 1,
                "Invalid trace sampling ratio.").ValidateOnStart();
        Sdk.SetDefaultTextMapPropagator(new TraceContextPropagator());
        System.Diagnostics.Activity.DefaultIdFormat = System.Diagnostics.ActivityIdFormat.W3C;
        System.Diagnostics.Activity.ForceDefaultIdFormat = true;
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.Clear().AddService("OrderFleet", serviceVersion: "1.0.0").AddTelemetrySdk())
            .WithTracing(traces =>
            {
                traces.AddSource(OrderFleetDiagnostics.Name).AddAspNetCoreInstrumentation(options =>
                    { options.RecordException = false; }).AddNpgsql().AddProcessor(new SafeActivityProcessor());
                var exporter = builder.Configuration.GetValue("Telemetry:Exporter", "Console");
                if (exporter == "Console") traces.AddConsoleExporter();
                else if (exporter == "Otlp") traces.AddOtlpExporter(options =>
                    options.Endpoint = new(builder.Configuration.GetValue("Telemetry:OtlpEndpoint", "http://localhost:4317")!));
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(OrderFleetDiagnostics.Name, OrderFleetDiagnostics.Name + ".Operations");
                var exporter = builder.Configuration.GetValue("Telemetry:Exporter", "Console");
                var interval = builder.Configuration.GetValue("Telemetry:MetricIntervalMilliseconds", 10000);
                if (exporter == "Console") metrics.AddConsoleExporter((_, reader) =>
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = interval);
                else if (exporter == "Otlp") metrics.AddOtlpExporter((options, reader) =>
                {
                    options.Endpoint = new(builder.Configuration.GetValue("Telemetry:OtlpEndpoint", "http://localhost:4317")!);
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = interval;
                });
            });
        // Sampling is resolved from the final host configuration. Instrumentation
        // and exporters must still register their services before provider creation.
        builder.Services.ConfigureOpenTelemetryTracerProvider((services, traces) =>
        {
            var ratio = services.GetRequiredService<IConfiguration>().GetValue("Telemetry:TraceSamplingRatio", 0.1);
            if (!double.IsFinite(ratio) || ratio is < 0 or > 1) ratio = 0;
            traces.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio)));
        });
    }
}
