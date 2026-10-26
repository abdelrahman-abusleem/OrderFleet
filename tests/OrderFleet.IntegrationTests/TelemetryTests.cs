using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using OrderFleet.Application.Diagnostics;
using OrderFleet.Infrastructure.Operations;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class TelemetryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task LegacyRequestIdentifier_CannotBreakOrderCommitOrOutboxTraceStorage()
    {
        await using var factory = new ApiFactory(fixture);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        { Content = JsonContent.Create(Request(restaurant, product)) };
        request.Headers.Add("Request-Id", "|" + new string('a', 500) + ".");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var response = await customer.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var context = fixture.CreateContext();
        var events = await context.OutboxMessages.Where(x => x.AggregateId == id).ToArrayAsync();
        Assert.NotEmpty(events);
        Assert.All(events, fact =>
        {
            if (fact.TraceParent is not null) Assert.True(ActivityContext.TryParse(fact.TraceParent, null, out _));
        });
    }

    [Fact]
    public async Task RealSdk_ExportsHttpDatabaseDispatchAndMetrics_WithTraceCorrelationAndNoSecrets()
    {
        var traces = new CapturedTraces();
        var metrics = new CapturedMetrics();
        var logs = new CapturedLogs();
        await using var factory = new ApiFactory(fixture);
        factory.CustomizeServices = services =>
        {
            services.AddSingleton<ILogEventSink>(logs);
            services.AddOpenTelemetry()
            .WithTracing(builder => builder.AddProcessor(new SimpleActivityExportProcessor(traces)))
            .WithMetrics(builder => builder.AddReader(new PeriodicExportingMetricReader(metrics, 60000)));
        };
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        const string secret = "never-export-this-private-marker";
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders?access_token=" + secret)
        { Content = JsonContent.Create(Request(restaurant, product)) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        request.Headers.Add("traceparent", $"00-{traceId}-00f067aa0ba902b7-01");
        request.Headers.Add("baggage", "private=" + secret);
        request.Headers.Add("User-Agent", secret);
        var response = await customer.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await DispatchConcurrencyTests.DispatchAsync(factory, id); // Real financial/state eligibility rejection is instrumented.
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/v1/operations/summary?access_token=" + secret)).StatusCode);
        await using (var context = await factory.Services.GetRequiredService<IDbContextFactory<OrderFleet.Infrastructure.Persistence.OrderFleetDbContext>>().CreateDbContextAsync())
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync($"SELECT {secret}::integer"));
        factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("OrderFleet.PrivacyProbe").LogInformation("Password {Password}", secret);
        using (var activity = OrderFleetDiagnostics.Activities.StartActivity("privacy.probe"))
        {
            Assert.NotNull(activity);
            activity.SetTag("password", secret);
            activity.SetBaggage("private", secret);
            activity.SetStatus(ActivityStatusCode.Error, secret);
            activity.AddException(new InvalidOperationException(secret));
        }
        await factory.Services.GetRequiredService<OperationalMetricSampler>().CollectAsync(CancellationToken.None);
        Assert.True(factory.Services.GetRequiredService<TracerProvider>().ForceFlush());
        Assert.True(factory.Services.GetRequiredService<MeterProvider>().ForceFlush());
        Assert.Contains(traces.Spans, x => x.TraceId == traceId && x.Source == "Microsoft.AspNetCore");
        Assert.Contains(traces.Spans, x => x.TraceId == traceId && x.Name == "order.create");
        Assert.Contains(traces.Spans, x => x.TraceId == traceId && x.Source == "Npgsql");
        Assert.Contains(traces.Spans, x => x.Name == "dispatch.assign");
        Assert.Contains(traces.Spans, x => x.Name == "privacy.probe");
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(traces.Spans));
        Assert.Contains(traces.Spans, x => x.Source == "Npgsql" && x.Status == ActivityStatusCode.Error);
        Assert.Contains(logs.Events, entry => entry.Contains("RoutePattern", StringComparison.Ordinal));
        Assert.Contains(logs.Events, entry => entry.Contains("\"SourceCategory\":\"Database\"", StringComparison.Ordinal)
            && entry.Contains("Operational incident", StringComparison.Ordinal) && entry.Contains("EventCode", StringComparison.Ordinal));
        Assert.DoesNotContain(secret, string.Join("\n", logs.Events));
        Assert.Contains("orderfleet.http.requests", metrics.Names);
        Assert.Contains("orderfleet.http.duration", metrics.Names);
        Assert.Contains("orderfleet.errors", metrics.Names);
        Assert.Contains("orderfleet.dispatch.results", metrics.Names);
        Assert.Contains("orderfleet.dispatch.duration", metrics.Names);
        Assert.Contains("orderfleet.outbox.pending", metrics.Names);
        Assert.Contains("orderfleet.outbox.oldest_lag", metrics.Names);
        Assert.Contains("orderfleet.worker.progress_age", metrics.Names);
        Assert.Contains("orderfleet.worker.ready", metrics.Names);
        Assert.Contains("orderfleet.recovery.pending", metrics.Names);
        await using var verify = fixture.CreateContext();
        var fact = await verify.OutboxMessages.SingleAsync(x => x.AggregateId == id && x.Type == "order.created.v1");
        Assert.True(ActivityContext.TryParse(fact.TraceParent, null, out var original));
        Assert.Equal(traceId, original.TraceId.ToString());
    }
}

internal sealed record CapturedSpan(string TraceId, string SpanId, string ParentSpanId, string Source, string Name,
    ActivityKind Kind, ActivityStatusCode Status, string? Description, IReadOnlyDictionary<string, string?> Tags, string EventData, string Baggage, string? TraceState);

internal sealed class CapturedTraces : BaseExporter<Activity>
{
    internal ConcurrentBag<CapturedSpan> Spans { get; } = new();
    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
            Spans.Add(new(activity.TraceId.ToString(), activity.SpanId.ToString(), activity.ParentSpanId.ToString(), activity.Source.Name,
                activity.DisplayName, activity.Kind, activity.Status, activity.StatusDescription,
                activity.TagObjects.ToDictionary(x => x.Key, x => x.Value?.ToString()),
                string.Join(";", activity.Events.SelectMany(x => x.Tags).Select(x => x.Value?.ToString())),
                string.Join(";", activity.Baggage.Select(x => x.Value)), activity.TraceStateString));
        return ExportResult.Success;
    }
}

internal sealed class CapturedLogs : ILogEventSink
{
    internal ConcurrentBag<string> Events { get; } = new();
    public void Emit(LogEvent logEvent)
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        new CompactJsonFormatter().Format(logEvent, writer);
        Events.Add(writer.ToString());
    }
}

internal sealed class CapturedMetrics : BaseExporter<Metric>
{
    internal ConcurrentBag<string> Names { get; } = new();
    public override ExportResult Export(in Batch<Metric> batch)
    {
        foreach (var metric in batch)
            foreach (ref readonly var point in metric.GetMetricPoints()) Names.Add(metric.Name);
        return ExportResult.Success;
    }
}
