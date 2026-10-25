using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
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
        services.AddScoped<IOrderReadStore, EfOrderReadStore>();
        services.AddScoped<GetOrder>();
        services.AddSingleton(TimeProvider.System);
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
        services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser().Build());
        services.AddRateLimiter(options =>
        {
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
                    PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                    AutoReplenishment = true
                }));
        });
        return services;
    }

    private static void ConfigureBearer(JwtBearerOptions options, JwtOptions jwt)
    {
        options.MapInboundClaims = false;
        options.IncludeErrorDetails = false;
        options.TokenValidationParameters = new()
        {
            ValidateIssuer = true, ValidIssuer = jwt.Issuer,
            ValidateAudience = true, ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true, RequireSignedTokens = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub", RoleClaimType = "role"
        };
        options.Events = new JwtBearerEvents
        {
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
            OnTokenValidated = context =>
            {
                if (!Guid.TryParse(context.Principal?.FindFirst("sub")?.Value, out var id) || id == Guid.Empty)
                    context.Fail("Invalid subject.");
                return Task.CompletedTask;
            }
        };
    }
}
