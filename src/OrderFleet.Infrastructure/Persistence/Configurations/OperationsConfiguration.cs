using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderFleet.Infrastructure.Operations;

namespace OrderFleet.Infrastructure.Persistence.Configurations;

public sealed class OperationRequestConfiguration : IEntityTypeConfiguration<OperationRequest>
{
    public void Configure(EntityTypeBuilder<OperationRequest> builder)
    {
        builder.ToTable("OperationRequests");
        builder.HasKey(x => new { x.ActorId, x.Operation, x.Key });
        builder.HasOne<IdentityUser<Guid>>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.Operation).HasMaxLength(40);
        builder.Property(x => x.Key).HasMaxLength(100);
    }
}
public sealed class OperationAuditConfiguration : IEntityTypeConfiguration<OperationAudit>
{
    public void Configure(EntityTypeBuilder<OperationAudit> builder)
    {
        builder.ToTable("OperationAudits");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Action).HasMaxLength(40);
        builder.Property(x => x.PreviousOutcome).HasMaxLength(100);
        builder.HasIndex(x => new { x.TargetId, x.OccurredAtUtc });
    }
}
