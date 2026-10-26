using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderFleet.Api.Errors;
using OrderFleet.Domain.Common;

namespace OrderFleet.IntegrationTests;

public sealed class HttpErrorMappingTests
{
    [Theory]
    [InlineData("concurrency", HttpStatusCode.Conflict)]
    [InlineData("domain", HttpStatusCode.Conflict)]
    [InlineData("argument", HttpStatusCode.BadRequest)]
    [InlineData("unexpected", HttpStatusCode.InternalServerError)]
    public async Task ExceptionBoundary_ReturnsSafeProblemDetails(string failure, HttpStatusCode expected)
    {
        const string privateDetail = "PRIVATE_INTERNAL_DETAIL";
        Exception error = failure switch
        {
            "concurrency" => new DbUpdateConcurrencyException(privateDetail),
            "domain" => new DomainException(privateDetail),
            "argument" => new ArgumentException(privateDetail),
            _ => new InvalidOperationException(privateDetail)
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<OrderFleetExceptionHandler>();
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapGet("/failure", (Func<string>)(() => throw error));
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/failure");

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(privateDetail, body);
        Assert.DoesNotContain("stackTrace", body, StringComparison.OrdinalIgnoreCase);
    }
}
