
using Microsoft.EntityFrameworkCore;
using OrderFleet.Infrastructure.Persistence;
using OrderFleet.Api.Errors;
using OrderFleet.Api.Identity;
using OrderFleet.Api.Endpoints;
using System.Text.Json.Serialization;
using OrderFleet.Api.Configuration;
using OrderFleet.Api.Realtime;
using OrderFleet.Application.Eta;
using OrderFleet.Application.Identity;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration
    .GetConnectionString("OrderFleet")
    ?? throw new InvalidOperationException(
        "OrderFleet connection string is missing.");

builder.Services.AddDbContextFactory<OrderFleetDbContext>(
    options => options.UseNpgsql(connectionString, postgres => postgres.UseNetTopologySuite()));

builder.Services.AddOrderFleetOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<OrderFleetExceptionHandler>();
builder.Services.AddOrderFleetIdentity(builder.Configuration);
builder.Services.AddOrdering(builder.Configuration);
builder.Services.AddCouriers(builder.Configuration);
builder.Services.AddMessaging(builder.Configuration);
builder.Services.AddDispatch(builder.Configuration);
builder.Services.AddRecovery(builder.Configuration);
builder.Services.AddEta(builder.Configuration);
builder.Services.AddOrderRealtime();
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Connections", LogLevel.Warning);
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
app.MapCourierEndpoints();
app.MapDispatchEndpoints();
app.MapHub<OrdersHub>("/hubs/orders", options =>
    { options.CloseOnAuthenticationExpiration = true; options.AllowStatefulReconnects = false; })
    .RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.All));
app.MapGet("/api/v1/orders/{id:guid}/eta", async (Guid id, ClaimsPrincipal principal, GetEta query, CancellationToken ct) =>
    Results.Ok(await query.ExecuteAsync(id, principal.ToActor(), ct))).RequireAuthorization();

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
