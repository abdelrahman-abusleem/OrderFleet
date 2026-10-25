
using Microsoft.EntityFrameworkCore;
using OrderFleet.Domain.Orders;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using OrderFleet.Application.Identity;
using OrderFleet.Infrastructure.Identity;
using OrderFleet.Domain.Restaurants;
using OrderFleet.Infrastructure.Orders;

namespace OrderFleet.Infrastructure.Persistence;

public sealed class OrderFleetDbContext : IdentityDbContext<IdentityUser<Guid>, IdentityRole<Guid>, Guid>
{
    public OrderFleetDbContext(
        DbContextOptions<OrderFleetDbContext> options)
        : base(options)
    {
    }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<RestaurantMembership> RestaurantMemberships => Set<RestaurantMembership>();
    public DbSet<Restaurant> Restaurants => Set<Restaurant>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<OrderDeliveryInformation> OrderDeliveryInformation => Set<OrderDeliveryInformation>();
    public DbSet<OrderCreationRequest> OrderCreationRequests => Set<OrderCreationRequest>();
    public DbSet<OrderAuditEntry> OrderAuditEntries => Set<OrderAuditEntry>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<IdentityUser<Guid>>().HasIndex(x => x.NormalizedEmail).IsUnique();
        var memberships = modelBuilder.Entity<RestaurantMembership>();
        memberships.HasKey(x => new { x.UserId, x.RestaurantId });
        memberships.HasOne<IdentityUser<Guid>>().WithMany().HasForeignKey(x => x.UserId);
        memberships.HasOne<Restaurant>().WithMany().HasForeignKey(x => x.RestaurantId);
        var roleIds = new[]
        {
            "c92cb45d-1e41-43c8-b5dc-000000000001", "c92cb45d-1e41-43c8-b5dc-000000000002",
            "c92cb45d-1e41-43c8-b5dc-000000000003", "c92cb45d-1e41-43c8-b5dc-000000000004",
            "c92cb45d-1e41-43c8-b5dc-000000000005"
        };
        modelBuilder.Entity<IdentityRole<Guid>>().HasData(OrderFleetRoles.All.Select((role, index) =>
            new IdentityRole<Guid>(role)
            {
                Id = Guid.Parse(roleIds[index]), NormalizedName = role.ToUpperInvariant(),
                ConcurrencyStamp = roleIds[index]
            }));

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(OrderFleetDbContext).Assembly);
    }
}
