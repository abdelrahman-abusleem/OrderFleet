using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderFleet.Api.Configuration;
using OrderFleet.Api.Diagnostics;
using OrderFleet.Api.Errors;
using Serilog;
using Serilog.Core;

namespace OrderFleet.IntegrationTests;

public sealed class SafeIncidentTests
{
    [Fact]
    public void UnknownWarningsAndExceptionLogs_ProduceCorrelatedSafeIncidents_WithoutRawContent()
    {
        const string marker = "secret-password-token-coordinates-never-export";
        var captured = new CapturedLogs();
        using var downstream = new LoggerConfiguration().WriteTo.Sink(captured).CreateLogger();
        using var logger = new LoggerConfiguration().WriteTo.Sink(new SafeLogSink(downstream)).CreateLogger();
        using var activity = new Activity("incident-test").SetIdFormat(ActivityIdFormat.W3C).Start();
        var contextual = logger.ForContext("SourceContext", "OrderFleet." + marker);
        contextual.Information("Private {Password}", marker);
        contextual.Warning(new IOException(marker), "unreviewed " + marker);
        contextual.Error(new InvalidOperationException(marker), "Another {Body}", marker);
        Assert.Equal(2, captured.Events.Count);
        Assert.All(captured.Events, entry =>
        {
            Assert.Contains("Operational incident", entry);
            Assert.Contains(activity.TraceId.ToString(), entry);
            Assert.DoesNotContain(marker, entry);
            Assert.DoesNotContain("StackTrace", entry);
        });
        Assert.Contains(captured.Events, x => x.Contains("IOException", StringComparison.Ordinal));
        Assert.Contains(captured.Events, x => x.Contains("InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnexpectedHttpFailure_IsRecordedOnce_WhileBusinessRejectionRemainsDistinct()
    {
        const string marker = "private-customer-body-credential";
        var captured = new CapturedLogs();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Telemetry:Exporter"] = "None" });
        builder.Services.AddSingleton<ILogEventSink>(captured);
        builder.AddOrderTelemetry(); builder.Services.AddProblemDetails(); builder.Services.AddExceptionHandler<OrderFleetExceptionHandler>();
        await using var app = builder.Build(); _ = app.Services.GetRequiredService<SafeExceptionListener>();
        app.UseMiddleware<SafeRequestLogging>(); app.UseExceptionHandler();
        app.MapGet("/unexpected", (Func<string>)(() => throw new InvalidOperationException(marker)));
        app.MapGet("/business", (Func<string>)(() => throw new OrderFleet.Domain.Orders.DomainException(marker)));
        await app.StartAsync(); using var client = app.GetTestClient();
        var unexpected = await client.GetAsync("/unexpected");
        Assert.Equal(HttpStatusCode.InternalServerError, unexpected.StatusCode);
        Assert.DoesNotContain(marker, await unexpected.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/business")).StatusCode);
        Assert.Single(captured.Events, x => x.Contains("Unhandled request failure", StringComparison.Ordinal));
        Assert.DoesNotContain(captured.Events, x => x.Contains("Operational incident", StringComparison.Ordinal));
        Assert.DoesNotContain(captured.Events, x => x.Contains("Request failed with", StringComparison.Ordinal));
        Assert.DoesNotContain(marker, string.Join('\n', captured.Events));
    }
}
