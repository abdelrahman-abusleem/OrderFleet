
using Microsoft.EntityFrameworkCore;
using OrderFleet.Domain.Orders;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using OrderFleet.Application.Identity;
using OrderFleet.Infrastructure.Identity;
using OrderFleet.Domain.Restaurants;
using OrderFleet.Infrastructure.Orders;
using OrderFleet.Domain.Payments;
using OrderFleet.Infrastructure.Payments;
using OrderFleet.Infrastructure.Messaging;
using OrderFleet.Domain.Couriers;
using OrderFleet.Infrastructure.Couriers;
using OrderFleet.Infrastructure.Dispatch;
using OrderFleet.Infrastructure.Operations;
using OrderFleet.Infrastructure.Recovery;

namespace OrderFleet.Infrastructure.Persistence;

public sealed class OrderFleetDbContext : IdentityDbContext<IdentityUser<Guid>, IdentityRole<Guid>, Guid>
{
    private static readonly IReadOnlyDictionary<string, Guid> RoleIds = new Dictionary<string, Guid>
    {
        [OrderFleetRoles.Customer] = new("c92cb45d-1e41-43c8-b5dc-000000000001"),
        [OrderFleetRoles.RestaurantStaff] = new("c92cb45d-1e41-43c8-b5dc-000000000002"),
        [OrderFleetRoles.RestaurantOwner] = new("c92cb45d-1e41-43c8-b5dc-000000000003"),
        [OrderFleetRoles.Courier] = new("c92cb45d-1e41-43c8-b5dc-000000000004"),
        [OrderFleetRoles.Admin] = new("c92cb45d-1e41-43c8-b5dc-000000000005")
    };

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
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<MockPaymentAuthorization> MockPaymentAuthorizations => Set<MockPaymentAuthorization>();
    public DbSet<MockPaymentEffect> MockPaymentEffects => Set<MockPaymentEffect>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<PaymentCaptureAttempt> PaymentCaptureAttempts => Set<PaymentCaptureAttempt>();
    public DbSet<FinancialAuditRecord> FinancialAuditRecords => Set<FinancialAuditRecord>();
    public DbSet<Courier> Couriers => Set<Courier>();
    public DbSet<CourierPosition> CourierPositions => Set<CourierPosition>();
    public DbSet<DispatchJob> DispatchJobs => Set<DispatchJob>();
    public DbSet<DispatchAttempt> DispatchAttempts => Set<DispatchAttempt>();
    public DbSet<CourierAssignment> CourierAssignments => Set<CourierAssignment>();
    public DbSet<OperationRequest> OperationRequests => Set<OperationRequest>();
    public DbSet<OperationAudit> OperationAudits => Set<OperationAudit>();
    public DbSet<RecoveryWorkFailure> RecoveryWorkFailures => Set<RecoveryWorkFailure>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasPostgresExtension("postgis");

        modelBuilder.Entity<IdentityUser<Guid>>().HasIndex(x => x.NormalizedEmail).IsUnique();
        var memberships = modelBuilder.Entity<RestaurantMembership>();
        memberships.HasKey(x => new { x.UserId, x.RestaurantId });
        memberships.HasOne<IdentityUser<Guid>>().WithMany().HasForeignKey(x => x.UserId);
        memberships.HasOne<Restaurant>().WithMany().HasForeignKey(x => x.RestaurantId);
        modelBuilder.Entity<IdentityRole<Guid>>().HasData(OrderFleetRoles.All.Select(role =>
            new IdentityRole<Guid>(role)
            {
                Id = RoleIds[role],
                NormalizedName = role.ToUpperInvariant(),
                ConcurrencyStamp = RoleIds[role].ToString("D")
            }));

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(OrderFleetDbContext).Assembly);
    }
}
