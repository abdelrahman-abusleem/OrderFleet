using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OrderFleet.Application.Identity;

namespace OrderFleet.Api.Identity;

public sealed record AccessToken(string AccessTokenValue, DateTimeOffset ExpiresAt);

public sealed class JwtTokenIssuer(IOptions<JwtOptions> options, TimeProvider clock)
{
    public AccessToken Issue(AuthenticatedAccount account)
    {
        var jwt = options.Value;
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(jwt.LifetimeMinutes);
        var claims = new List<Claim>
        {
            new("sub", account.Id.ToString()), new("jti", Guid.NewGuid().ToString("N")),
            new("iat", now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
        };
        claims.AddRange(account.Roles.Select(role => new Claim("role", role)));
        var token = new JwtSecurityToken(jwt.Issuer, jwt.Audience, claims,
            now.UtcDateTime, expires.UtcDateTime,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                SecurityAlgorithms.HmacSha256));
        return new(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
