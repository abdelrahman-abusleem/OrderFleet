namespace OrderFleet.Api.Identity;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";
    public string SigningKey { get; set; } = "";
    public int LifetimeMinutes { get; set; } = 15;
}
