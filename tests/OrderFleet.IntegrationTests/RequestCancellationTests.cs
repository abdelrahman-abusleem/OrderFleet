using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderFleet.Api.Errors;

namespace OrderFleet.IntegrationTests;

public sealed class RequestCancellationTests
{
    [Fact]
    [Trait("Phase", "2")]
    public async Task ExceptionHandler_AbortedRequestWritesNoErrorOrBody()
    {
        using var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, RequestAborted = new CancellationToken(true) };
        await using var body = new MemoryStream();
        context.Response.Body = body;
        var logger = new RecordedLogger();
        var handler = new OrderFleetExceptionHandler(services.GetRequiredService<IProblemDetailsService>(), logger);
        Assert.True(await handler.TryHandleAsync(context, new OperationCanceledException(context.RequestAborted), CancellationToken.None));
        Assert.NotEqual(500, context.Response.StatusCode);
        Assert.Equal(0, body.Length);
        Assert.DoesNotContain(logger.Levels, level => level >= LogLevel.Error);
    }

    [Theory]
    [InlineData(true, 499)]
    [InlineData(false, 500)]
    public async Task ApiBoundary_DistinguishesClientAbortFromUnrelatedCancellation(bool aborted, int expectedStatus)
    {
        var logger = new RecordedLogger();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new RecordedProvider(logger));
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<OrderFleetExceptionHandler>();
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapGet("/cancelled", (Func<HttpContext, string>)(context =>
        {
            if (aborted) context.RequestAborted = new CancellationToken(true);
            throw new OperationCanceledException(context.RequestAborted);
        }));
        await app.StartAsync();
        using var client = app.GetTestClient();
        var response = await client.GetAsync("/cancelled");
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        if (aborted)
        {
            Assert.Empty(await response.Content.ReadAsStringAsync());
            Assert.DoesNotContain(logger.Levels, level => level >= LogLevel.Error);
        }
        else Assert.Contains(logger.Levels, level => level >= LogLevel.Error);
    }

    private sealed class RecordedLogger : ILogger<OrderFleetExceptionHandler>
    {
        internal System.Collections.Concurrent.ConcurrentBag<LogLevel> Levels { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) => Levels.Add(level);
    }
    private sealed class RecordedProvider(RecordedLogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }
}
