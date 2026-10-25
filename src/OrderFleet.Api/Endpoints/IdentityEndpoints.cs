using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using OrderFleet.Api.Identity;
using OrderFleet.Application.Identity;

namespace OrderFleet.Api.Endpoints;

public static class IdentityEndpoints
{
    public sealed record Credentials(string? Email, string? Password);

    public static void MapIdentityEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/auth");
        auth.MapPost("/register", RegisterAsync).AllowAnonymous().RequireRateLimiting("credentials");
        auth.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting("credentials");
        auth.MapGet("/me", (ClaimsPrincipal principal) => Results.Ok(new
        {
            id = Guid.Parse(principal.FindFirst("sub")!.Value),
            roles = principal.FindAll("role").Select(x => x.Value).ToArray()
        })).RequireAuthorization();
    }

    private static async Task<IResult> RegisterAsync(Credentials request, IIdentityAccounts accounts,
        CancellationToken cancellationToken)
    {
        if (!Valid(request)) return Results.Problem(statusCode: 400, title: "Invalid registration details.");
        var result = await accounts.RegisterAsync(request.Email!.Trim(), request.Password!, cancellationToken);
        return result == RegistrationOutcome.Accepted
            ? Results.Accepted(value: new { message = "Registration request processed." })
            : Results.Problem(statusCode: 400, title: "Invalid registration details.");
    }

    private static async Task<IResult> LoginAsync(Credentials request, IIdentityAccounts accounts,
        JwtTokenIssuer issuer, CancellationToken cancellationToken)
    {
        if (!Valid(request)) return Results.Problem(statusCode: 401, title: "Invalid credentials.");
        var account = await accounts.AuthenticateAsync(request.Email!.Trim(), request.Password!, cancellationToken);
        if (account is null) return Results.Problem(statusCode: 401, title: "Invalid credentials.");
        var token = issuer.Issue(account);
        return Results.Ok(new { accessToken = token.AccessTokenValue, token.ExpiresAt });
    }

    private static bool Valid(Credentials request) =>
        request.Email is { Length: > 0 and <= 256 } && new EmailAddressAttribute().IsValid(request.Email.Trim())
        && request.Password is { Length: > 0 and <= 128 };
}
