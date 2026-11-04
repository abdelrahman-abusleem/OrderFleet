using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Identity;
using OrderFleet.Infrastructure.Identity;

namespace OrderFleet.Infrastructure.Persistence;

public sealed class EfIdentityAdministrationStore(OrderFleetDbContext context) : IIdentityAdministrationStore
{
    public async Task ChangeRoleAsync(Guid actorId, Guid targetId, string role, bool grant, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockUsersAsync(actorId, targetId, ct);
        if (!await HasRoleAsync(actorId, OrderFleetRoles.Admin, ct)) throw new AccessDeniedException();
        var roleId = await context.Roles.Where(x => x.Name == role).Select(x => x.Id).SingleAsync(ct);
        var existing = await context.UserRoles.SingleOrDefaultAsync(x => x.UserId == targetId && x.RoleId == roleId, ct);
        if (grant && existing is null) context.UserRoles.Add(new IdentityUserRole<Guid> { UserId = targetId, RoleId = roleId });
        else if (!grant && existing is not null) context.UserRoles.Remove(existing);
        else return;
        context.OperationAudits.Add(new(actorId, (grant ? "GrantRole:" : "RevokeRole:") + role, targetId, now, null, null));
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task ChangeMembershipAsync(Guid actorId, Guid restaurantId, Guid targetId, bool grant, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await LockUsersAsync(actorId, targetId, ct);
        if (!await HasRoleAsync(actorId, OrderFleetRoles.RestaurantOwner, ct)) throw new AccessDeniedException();
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Restaurants\" WHERE \"Id\"={restaurantId} FOR UPDATE", ct);
        if (!await context.RestaurantMemberships.AnyAsync(x => x.UserId == actorId && x.RestaurantId == restaurantId, ct))
            throw new ResourceNotFoundException();
        if (grant && !await HasRoleAsync(targetId, OrderFleetRoles.RestaurantOwner, ct) && !await HasRoleAsync(targetId, OrderFleetRoles.RestaurantStaff, ct))
            throw new RequestValidationException("User is not eligible for restaurant membership.");
        var existing = await context.RestaurantMemberships.SingleOrDefaultAsync(x => x.UserId == targetId && x.RestaurantId == restaurantId, ct);
        if (grant && existing is null) context.RestaurantMemberships.Add(new RestaurantMembership(targetId, restaurantId));
        else if (!grant && existing is not null) context.RestaurantMemberships.Remove(existing);
        else return;
        context.OperationAudits.Add(new(actorId, grant ? "MembershipGranted" : "MembershipRevoked", restaurantId, now, null, targetId.ToString("D")));
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task LockUsersAsync(Guid actorId, Guid targetId, CancellationToken ct)
    {
        foreach (var id in new[] { actorId, targetId }.Distinct().OrderBy(id => id))
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\"={id} FOR UPDATE", ct);
        if (!await context.Users.AnyAsync(x => x.Id == targetId, ct)) throw new ResourceNotFoundException();
    }

    private Task<bool> HasRoleAsync(Guid userId, string role, CancellationToken ct) =>
        (from grant in context.UserRoles.AsNoTracking()
         join definition in context.Roles on grant.RoleId equals definition.Id
         where grant.UserId == userId && definition.Name == role
         select grant.UserId).AnyAsync(ct);
}
