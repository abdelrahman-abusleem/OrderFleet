
using Microsoft.EntityFrameworkCore;
using OrderFleet.Infrastructure.Persistence;
using OrderFleet.Api.Errors;
using OrderFleet.Api.Identity;
using OrderFleet.Api.Endpoints;
using System.Text.Json.Serialization;
using OrderFleet.Api.Configuration;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration
    .GetConnectionString("OrderFleet")
    ?? throw new InvalidOperationException(
        "OrderFleet connection string is missing.");

builder.Services.AddDbContext<OrderFleetDbContext>(
    options => options.UseNpgsql(connectionString));

builder.Services.AddOrderFleetOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<OrderFleetExceptionHandler>();
builder.Services.AddOrderFleetIdentity(builder.Configuration);
builder.Services.AddOrdering(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);

var app = builder.Build();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi().AllowAnonymous();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapIdentityEndpoints();
app.MapOrderReadEndpoints();
app.MapCatalogEndpoints();
app.MapOrderWriteEndpoints();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild",
    "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5)
        .Select(index => new WeatherForecast(
            DateOnly.FromDateTime(
                DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(
                summaries.Length)]
        ))
        .ToArray();

    return forecast;
})
.WithName("GetWeatherForecast");

app.Run();

record WeatherForecast(
    DateOnly Date,
    int TemperatureC,
    string? Summary)
{
    public int TemperatureF =>
        32 + (int)(TemperatureC / 0.5556);
}

public partial class Program;
