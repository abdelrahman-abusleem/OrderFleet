namespace OrderFleet.Application.Identity;

public static class OrderFleetRoles
{
    public const string Customer = "Customer";
    public const string RestaurantStaff = "RestaurantStaff";
    public const string RestaurantOwner = "RestaurantOwner";
    public const string Courier = "Courier";
    public const string Admin = "Admin";
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
        new[] { Customer, RestaurantStaff, RestaurantOwner, Courier, Admin });
}

public sealed record AuthenticatedAccount(Guid Id, IReadOnlyList<string> Roles);
public enum RegistrationOutcome { Accepted, Invalid }

public interface IIdentityAccounts
{
    Task<RegistrationOutcome> RegisterAsync(string email, string password, CancellationToken cancellationToken);
    Task<AuthenticatedAccount?> AuthenticateAsync(string email, string password, CancellationToken cancellationToken);
}

public sealed record Actor(Guid Id, IReadOnlySet<string> Roles)
{
    public bool IsInRole(string role) => Roles.Contains(role);
}
