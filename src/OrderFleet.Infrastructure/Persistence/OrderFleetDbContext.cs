
using Microsoft.EntityFrameworkCore;
using OrderFleet.Domain.Orders;

namespace OrderFleet.Infrastructure.Persistence;

public sealed class OrderFleetDbContext : DbContext
{
    public OrderFleetDbContext(
        DbContextOptions<OrderFleetDbContext> options)
        : base(options)
    {
    }

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(OrderFleetDbContext).Assembly);
    }
}
