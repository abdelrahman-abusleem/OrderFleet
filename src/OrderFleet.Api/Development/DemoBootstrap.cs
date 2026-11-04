using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OrderFleet.Application.Identity;
using OrderFleet.Domain.Couriers;
using OrderFleet.Domain.Geo;
using OrderFleet.Domain.Restaurants;
using OrderFleet.Infrastructure.Identity;
using OrderFleet.Infrastructure.Persistence;

namespace OrderFleet.Api.Development;


public static class DemoBootstrap
{
    public sealed record Manifest(Guid RestaurantId, Guid ProductId);
    public static async Task<Manifest> ExecuteAsync(IServiceProvider services, IHostEnvironment environment,
        string? password, CancellationToken ct)
    {
        if (!environment.IsDevelopment()) throw new InvalidOperationException("Demo seed requires Development environment.");
        if (password is null || password.Length is < 20 or > 128)
            throw new InvalidOperationException("A private Demo:Password of 20..128 characters is required.");
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OrderFleetDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(796243901)", ct);
        var accounts = new Dictionary<string, IdentityUser<Guid>>();
        var names = new[] { "customer", "owner", "staff", "courier", "admin", "stranger" };
        var roles = new[] { OrderFleetRoles.Customer, OrderFleetRoles.RestaurantOwner, OrderFleetRoles.RestaurantStaff,
            OrderFleetRoles.Courier, OrderFleetRoles.Admin, OrderFleetRoles.Customer };
        for (var index = 0; index < names.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var email = names[index] + "@demo.orderfleet.test";
            var expected = Guid.Parse($"de000000-0000-4000-8000-{index + 1:000000000000}");
            var account = await users.FindByEmailAsync(email);

            if (account is not null && account.Id != expected)
                throw new InvalidOperationException("Demo email already belongs to another identity; seed aborted.");
            if (account is null)
            {
                account = new() { Id = expected, UserName = email, Email = email };
                Check(await users.CreateAsync(account, password));
            }
            if (!await users.IsInRoleAsync(account, roles[index])) Check(await users.AddToRoleAsync(account, roles[index]));
            accounts.Add(names[index], account);
        }
        var owner = accounts["owner"].Id;
        var restaurant = await context.Restaurants.SingleOrDefaultAsync(r => r.Name == "OrderFleet Demo Kitchen"
            && context.RestaurantMemberships.Any(m => m.RestaurantId == r.Id && m.UserId == owner), ct);
        if (restaurant is null)
        {
            restaurant = Restaurant.Create("OrderFleet Demo Kitchen", new Coordinates(24.7136m, 46.6753m));
            context.Restaurants.Add(restaurant);
        }
        foreach (var name in new[] { "owner", "staff" })
        {
            var userId = accounts[name].Id;
            if (!await context.RestaurantMemberships.AnyAsync(m => m.UserId == userId && m.RestaurantId == restaurant.Id, ct))
                context.RestaurantMemberships.Add(new RestaurantMembership(userId, restaurant.Id));
        }
        var product = await context.Products.SingleOrDefaultAsync(p => p.RestaurantId == restaurant.Id && p.Name == "Demo Meal", ct);
        if (product is null)
        {
            product = Product.Create(restaurant.Id, "Demo Meal", 25.50m);
            context.Products.Add(product);
        }
        var courierId = accounts["courier"].Id;
        if (!await context.Couriers.AnyAsync(c => c.Id == courierId, ct)) context.Couriers.Add(Courier.Register(courierId));
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(restaurant.Id, product.Id);
    }
    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Demo identity provisioning failed; inspect password policy and existing account state.");
    }
}
