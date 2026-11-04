using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderFleet.Application.Identity;
using OrderFleet.Infrastructure.Persistence;

namespace OrderFleet.Infrastructure.Identity;

public sealed class IdentityAccounts(UserManager<IdentityUser<Guid>> users, OrderFleetDbContext context)
    : IIdentityAccounts
{
    private static readonly string DummyHash = new PasswordHasher<IdentityUser<Guid>>()
        .HashPassword(new(), Guid.NewGuid().ToString("N"));

    public async Task<RegistrationOutcome> RegisterAsync(string email, string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var user = new IdentityUser<Guid> { Id = Guid.NewGuid(), UserName = email, Email = email };
        try
        {
            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded)
                return created.Errors.Any(x => x.Code is "DuplicateEmail" or "DuplicateUserName")
                    ? RegistrationOutcome.Accepted : RegistrationOutcome.Invalid;
            var role = await users.AddToRoleAsync(user, OrderFleetRoles.Customer);
            if (!role.Succeeded) throw new InvalidOperationException("Could not provision the customer role.");
            await transaction.CommitAsync(cancellationToken);
            return RegistrationOutcome.Accepted;
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation })
        {

            return RegistrationOutcome.Accepted;
        }
    }

    public async Task<AuthenticatedAccount?> AuthenticateAsync(string email, string password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            _ = users.PasswordHasher.VerifyHashedPassword(new(), DummyHash, password);
            return null;
        }
        if (await users.IsLockedOutAsync(user)) return null;
        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            return null;
        }
        var reset = await users.ResetAccessFailedCountAsync(user);
        if (!reset.Succeeded) return null;
        return new(user.Id, (await users.GetRolesAsync(user)).ToArray());
    }
}
