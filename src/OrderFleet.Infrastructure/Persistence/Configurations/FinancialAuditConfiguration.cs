using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderFleet.Infrastructure.Payments;
using OrderFleet.Domain.Common;

namespace OrderFleet.Infrastructure.Persistence.Configurations;

public sealed class FinancialAuditConfiguration : IEntityTypeConfiguration<FinancialAuditRecord>
{
    public void Configure(EntityTypeBuilder<FinancialAuditRecord> builder)
    {
        builder.ToTable("FinancialAuditRecords", table =>
        {
            table.HasCheckConstraint("CK_FinancialAudit_Amount", "\"Amount\" > 0 AND \"Amount\" <= 79228162514264337593543950335 AND round(\"Amount\",2) = \"Amount\"");
            table.HasCheckConstraint("CK_FinancialAudit_Currency", $"\"Currency\" = '{CurrencyCodes.Sar}'");
        });
        builder.HasKey(x => x.MessageId);
        builder.Property(x => x.Type).HasMaxLength(100).IsRequired();
        builder.Property(x => x.State).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.Amount).HasColumnType("numeric");
        builder.HasIndex(x => new { x.OrderId, x.OccurredAtUtc });
        // Retain financial history independently of future operational row retention.
    }
}
