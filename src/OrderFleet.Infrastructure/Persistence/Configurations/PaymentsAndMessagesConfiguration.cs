using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OrderFleet.Domain.Orders;
using OrderFleet.Domain.Payments;
using OrderFleet.Infrastructure.Messaging;
using OrderFleet.Infrastructure.Payments;

namespace OrderFleet.Infrastructure.Persistence.Configurations;

public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments", table => table.HasCheckConstraint("CK_Payments_Amount",
            "\"Amount\" > 0 AND \"Amount\" <= 79228162514264337593543950335 AND \"Amount\" = trunc(\"Amount\", 2)"));
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.OrderId).IsUnique();
        builder.HasIndex(x => x.AuthorizationId).IsUnique();


        builder.HasIndex(x => new { x.AuthorizedAt, x.Id }).HasDatabaseName("IX_Payments_PendingReconciliation")
            .HasFilter("\"State\" = 'PendingReconciliation'").IncludeProperties(x => x.OrderId);
        builder.HasOne<Order>().WithOne().HasForeignKey<Payment>(x => x.OrderId);
        builder.HasOne<MockPaymentAuthorization>().WithOne().HasForeignKey<Payment>(x => x.AuthorizationId);
        builder.Property(x => x.Amount).HasColumnType("numeric");
        builder.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        builder.Property<uint>("xmin").IsRowVersion();
        var utc = new ValueConverter<DateTimeOffset, DateTimeOffset>(value => value.ToUniversalTime(), value => value);
        builder.Property(x => x.AuthorizedAt).HasConversion(utc);
        builder.Property(x => x.CapturedAt).HasConversion(utc);
        builder.Property(x => x.FailedAt).HasConversion(utc);
        builder.Property(x => x.SettledAt).HasConversion(utc);
    }
}

public sealed class MockAuthorizationConfiguration : IEntityTypeConfiguration<MockPaymentAuthorization>
{
    public void Configure(EntityTypeBuilder<MockPaymentAuthorization> builder)
    {
        builder.ToTable("MockPaymentAuthorizations", table => table.HasCheckConstraint("CK_MockAuthorization_Amount",
            "\"Amount\" > 0 AND \"Amount\" <= 79228162514264337593543950335 AND \"Amount\" = trunc(\"Amount\", 2)"));
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.ActorId, x.Key }).IsUnique();
        builder.HasIndex(x => new { x.State, x.CreatedAt });
        builder.Property(x => x.Key).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Amount).HasColumnType("numeric");
        builder.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        builder.Property<uint>("xmin").IsRowVersion();
    }
}

public sealed class MockEffectConfiguration : IEntityTypeConfiguration<MockPaymentEffect>
{
    public void Configure(EntityTypeBuilder<MockPaymentEffect> builder)
    {
        builder.ToTable("MockPaymentEffects");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.AuthorizationId, x.Operation }).IsUnique();
        builder.HasOne<MockPaymentAuthorization>().WithMany().HasForeignKey(x => x.AuthorizationId);
        builder.Property(x => x.Operation).HasMaxLength(20).IsRequired();
    }
}

public sealed class CaptureAttemptConfiguration : IEntityTypeConfiguration<PaymentCaptureAttempt>
{
    public void Configure(EntityTypeBuilder<PaymentCaptureAttempt> builder)
    {
        builder.ToTable("PaymentCaptureAttempts");
        builder.HasKey(x => x.Id);
        builder.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId);
        builder.HasIndex(x => new { x.PaymentId, x.OccurredAtUtc });
        builder.Property(x => x.IdempotencyKey).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Classification).HasConversion<string>().HasMaxLength(32);
    }
}

public sealed class OutboxConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Sequence).UseIdentityAlwaysColumn();
        builder.HasIndex(x => x.Sequence).IsUnique();
        builder.HasIndex(x => new { x.AggregateId, x.AggregateSequence }).IsUnique()
            .HasFilter("\"AggregateSequence\" IS NOT NULL");
        builder.HasIndex(x => new { x.PublishedAtUtc, x.DeadLetteredAtUtc, x.NextAttemptAtUtc });
        builder.Property(x => x.Type).HasMaxLength(100).IsRequired();
        builder.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(64);
        builder.Property(x => x.TraceParent).HasMaxLength(55);
        builder.Property(x => x.LastErrorCode).HasMaxLength(100);
    }
}

public sealed class InboxConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("InboxMessages");
        builder.HasKey(x => new { x.MessageId, x.Consumer });
        builder.Property(x => x.Consumer).HasMaxLength(100);
    }
}
