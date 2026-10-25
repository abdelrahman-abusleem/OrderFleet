
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
using OrderFleet.Api.Diagnostics;

if (args.Length == 1 && args[0] == "--health-check")
{
    Environment.ExitCode = await ContainerHealthProbe.ExecuteAsync();
    return;
}
var migrateOnly = args.Contains("--migrate", StringComparer.Ordinal);
var seedOnly = args.Contains("--seed-demo", StringComparer.Ordinal);
if (migrateOnly && seedOnly) throw new InvalidOperationException("Run migration and seed as separate explicit operations.");
var builder = WebApplication.CreateBuilder(args.Where(arg => arg is not ("--migrate" or "--seed-demo")).ToArray());
builder.AddOrderTelemetry();

var connectionString = builder.Configuration
    .GetConnectionString("OrderFleet")
    ?? throw new InvalidOperationException(
        "OrderFleet connection string is missing.");

builder.Services.AddDbContextFactory<OrderFleetDbContext>(
    options => options.UseNpgsql(connectionString, postgres =>
    {
        postgres.UseNetTopologySuite();
        postgres.ConfigureDataSource(source => source.ConfigureTracing(trace => trace
            .ConfigureCommandSpanNameProvider(_ => "postgresql.command")
            .ConfigureBatchSpanNameProvider(_ => "postgresql.batch").EnableFirstResponseEvent(false)));
    }));

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
builder.Services.AddOperations();
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Connections", LogLevel.Warning);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);

var app = builder.Build();
_ = app.Services.GetRequiredService<SafeExceptionListener>();
if (migrateOnly)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<OrderFleetDbContext>().Database.MigrateAsync();
    Console.WriteLine("Database migrations applied.");
    return;
}
if (seedOnly)
{
    var manifest = await OrderFleet.Api.Development.DemoBootstrap.ExecuteAsync(app.Services, app.Environment,
        app.Configuration["Demo:Password"], CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(manifest));
    return;
}
app.UseMiddleware<SafeRequestLogging>();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi().AllowAnonymous();
}

if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing")) app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapIdentityEndpoints();
app.MapOrderReadEndpoints();
app.MapCatalogEndpoints();
app.MapOrderWriteEndpoints();
app.MapCourierEndpoints();
app.MapDispatchEndpoints();
app.MapOperationsEndpoints();
app.MapHealth();
app.MapHub<OrdersHub>("/hubs/orders", options =>
    { options.CloseOnAuthenticationExpiration = true; options.AllowStatefulReconnects = false; })
    .RequireAuthorization(policy => policy.RequireRole(OrderFleetRoles.All));
app.MapGet("/api/v1/orders/{id:guid}/eta", async (Guid id, ClaimsPrincipal principal, GetEta query, CancellationToken ct) =>
    Results.Ok(await query.ExecuteAsync(id, principal.ToActor(), ct))).RequireAuthorization();

app.Run();

public partial class Program;
