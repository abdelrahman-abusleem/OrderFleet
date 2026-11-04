using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using OrderFleet.Application.Identity;
using OrderFleet.Application.Orders;
using OrderFleet.Infrastructure.Identity;
using OrderFleet.Infrastructure.Persistence;

namespace OrderFleet.Api.Identity;

public static class IdentityRegistration
{
    public static IServiceCollection AddOrderFleetIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddIdentityCore<IdentityUser<Guid>>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 12;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        }).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<OrderFleetDbContext>();
        services.AddScoped<IIdentityAccounts, IdentityAccounts>();
        services.AddScoped<IIdentityAdministrationStore, EfIdentityAdministrationStore>();
        services.AddScoped<ManageIdentityAccess>();
        services.AddScoped<IOrderReadStore, EfOrderReadStore>();
        services.AddScoped<GetOrder>();
        services.AddScoped<OrderAccessPolicy>();
        AddJwtAuthentication(services, configuration);
        services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser().Build());
        AddRequestRateLimiting(services, configuration);
        return services;
    }

    private static void AddJwtAuthentication(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<JwtTokenIssuer>();
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection("Jwt"))
            .Validate(x => Uri.TryCreate(x.Issuer, UriKind.Absolute, out _), "Jwt issuer is required.")
            .Validate(x => !string.IsNullOrWhiteSpace(x.Audience), "Jwt audience is required.")
            .Validate(x => Encoding.UTF8.GetByteCount(x.SigningKey) >= 32, "Jwt signing key must contain at least 32 bytes.")
            .Validate(x => x.LifetimeMinutes is >= 1 and <= 60, "Jwt lifetime must be between 1 and 60 minutes.")
            .ValidateOnStart();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((options, configured) => ConfigureBearer(options, configured.Value));
    }

    private static void AddRequestRateLimiting(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RequestLimitOptions>().Bind(configuration.GetSection("RequestLimits"))
            .Validate(value => value.IsValid(), "Invalid request rate limits.").ValidateOnStart();
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RequestLimitOptions>>((options, configured) =>
        {
            var limits = configured.Value;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var path = context.Request.Path;
                if ((!path.StartsWithSegments("/api/v1") && !path.StartsWithSegments("/hubs/orders"))
                    || path == "/api/v1/auth/login" || path == "/api/v1/auth/register")
                    return RateLimitPartition.GetNoLimiter("other");
                var read = HttpMethods.IsGet(context.Request.Method) || path.StartsWithSegments("/hubs/orders");
                var create = HttpMethods.IsPost(context.Request.Method)
                    && string.Equals(path.Value?.TrimEnd('/'), "/api/v1/orders", StringComparison.OrdinalIgnoreCase);
                var bucket = create ? "create" : read ? "read" : "write";
                var actor = context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(bucket + ":" + actor, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = create ? limits.CreatePerMinute : read ? limits.ReadPerMinute : limits.WritePerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                });
            });
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many requests."
                }, options: null, contentType: "application/problem+json", cancellationToken: cancellationToken);
            };
            options.AddPolicy("credentials", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
            options.AddPolicy("health", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }

    private static void ConfigureBearer(JwtBearerOptions options, JwtOptions jwt)
    {
        options.MapInboundClaims = false;
        options.IncludeErrorDetails = false;
        options.TokenValidationParameters = new()
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Path.StartsWithSegments("/hubs/orders")
                    && (context.HttpContext.WebSockets.IsWebSocketRequest || context.Request.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.Ordinal)))
                {
                    var token = context.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(token)) context.Token = token;
                }
                return Task.CompletedTask;
            },
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>()
                    .WriteAsync(new ProblemDetailsContext
                    {
                        HttpContext = context.HttpContext,
                        ProblemDetails = new() { Status = 401, Title = "Authentication required." }
                    });
            },
            OnForbidden = async context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>()
                    .WriteAsync(new ProblemDetailsContext
                    {
                        HttpContext = context.HttpContext,
                        ProblemDetails = new() { Status = 403, Title = "Access denied." }
                    });
            },
            OnTokenValidated = async context =>
            {
                if (!Guid.TryParse(context.Principal?.FindFirst("sub")?.Value, out var id) || id == Guid.Empty)
                {
                    context.Fail("Invalid subject.");
                    return;
                }
                var privileged = context.Principal!.FindAll("role").Where(claim => claim.Value is OrderFleetRoles.Admin
                    or OrderFleetRoles.RestaurantOwner or OrderFleetRoles.RestaurantStaff or OrderFleetRoles.Courier).ToArray();
                if (privileged.Length > 0)
                {
                    var database = context.HttpContext.RequestServices.GetRequiredService<OrderFleetDbContext>();
                    var current = await (from grant in database.UserRoles.AsNoTracking()
                                         join role in database.Roles on grant.RoleId equals role.Id
                                         where grant.UserId == id
                                         select role.Name).ToArrayAsync(context.HttpContext.RequestAborted);
                    foreach (var identity in context.Principal.Identities.OfType<ClaimsIdentity>())
                        foreach (var claim in identity.FindAll("role").Where(claim => privileged.Contains(claim) && !current.Contains(claim.Value, StringComparer.Ordinal)).ToArray())
                            identity.RemoveClaim(claim);
                }
            }
        };
    }
}
