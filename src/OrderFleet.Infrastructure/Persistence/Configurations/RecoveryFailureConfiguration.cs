using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderFleet.Infrastructure.Recovery;

namespace OrderFleet.Infrastructure.Persistence.Configurations;

public sealed class RecoveryFailureConfiguration : IEntityTypeConfiguration<RecoveryWorkFailure>
{
    public void Configure(EntityTypeBuilder<RecoveryWorkFailure> builder)
    {
        builder.ToTable("RecoveryWorkFailures");
        builder.HasKey(x => new { x.Operation, x.ResourceId });
        builder.Property(x => x.Operation).HasMaxLength(40);
        builder.Property(x => x.LastErrorCode).HasMaxLength(40);
        builder.HasIndex(x => new { x.ResolvedAtUtc, x.DeadLetteredAtUtc, x.NextAttemptAtUtc });
    }
}
