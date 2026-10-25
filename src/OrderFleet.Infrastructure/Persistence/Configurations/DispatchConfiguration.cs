using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderFleet.Domain.Orders;
using OrderFleet.Infrastructure.Dispatch;

namespace OrderFleet.Infrastructure.Persistence.Configurations;

public sealed class DispatchJobConfiguration : IEntityTypeConfiguration<DispatchJob>
{
    public void Configure(EntityTypeBuilder<DispatchJob> builder)
    {
        builder.ToTable("DispatchJobs");
        builder.HasKey(x => x.OrderId);
        builder.HasOne<Order>().WithOne().HasForeignKey<DispatchJob>(x => x.OrderId);
        builder.Property(x => x.Status).HasMaxLength(20);
        builder.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
        builder.Property<uint>("xmin").IsRowVersion();
    }
}

public sealed class DispatchAttemptConfiguration : IEntityTypeConfiguration<DispatchAttempt>
{
    public void Configure(EntityTypeBuilder<DispatchAttempt> builder)
    {
        builder.ToTable("DispatchAttempts");
        builder.HasKey(x => x.Id);
        builder.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId);
        builder.Property(x => x.Outcome).HasMaxLength(32);
        builder.HasIndex(x => new { x.OrderId, x.OccurredAtUtc });
    }
}
