
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OrderFleet.Domain.Orders;

namespace OrderFleet.Infrastructure.Persistence.Configurations;

public sealed class OrderConfiguration
    : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders", table => table.HasCheckConstraint("CK_Orders_TotalAmount",
            "\"TotalAmount\" > 0 AND \"TotalAmount\" <= 79228162514264337593543950335 " +
            "AND \"TotalAmount\" = trunc(\"TotalAmount\", 2)"));

        builder.Ignore(order => order.DomainEvents);

        var utcTimestamp = new ValueConverter<DateTimeOffset, DateTimeOffset>(
            value => value.ToUniversalTime(), value => value);
        builder.Property(order => order.CreatedAt).HasConversion(utcTimestamp);
        builder.Property(order => order.AcceptedAt).HasConversion(utcTimestamp);
        builder.Property(order => order.AssignedAt).HasConversion(utcTimestamp);
        builder.Property(order => order.PickedUpAt).HasConversion(utcTimestamp);
        builder.Property(order => order.DeliveredAt).HasConversion(utcTimestamp);
        builder.Property(order => order.CancelledAt).HasConversion(utcTimestamp);

        builder.HasKey(order => order.Id);

        builder.Property<uint>("xmin").IsRowVersion();

        builder.Property(order => order.CustomerId)
            .IsRequired();

        builder.Property(order => order.RestaurantId)
            .IsRequired();

        builder.Property(order => order.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(order => order.CancelReason)
            .HasConversion<string>()
            .HasMaxLength(40);

        builder.Property(order => order.CourierId);

        builder.Property(order => order.AssignmentId);

        builder.Property(order => order.Currency)
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(order => order.TotalAmount)
            .HasColumnType("numeric")
            .IsRequired();

        builder.OwnsMany(order => order.Items, item =>
        {
            item.ToTable("OrderItems", table =>
            {
                table.HasCheckConstraint("CK_OrderItems_UnitPrice",
                    "\"UnitPrice\" > 0 AND \"UnitPrice\" <= 79228162514264337593543950335 " +
                    "AND \"UnitPrice\" = trunc(\"UnitPrice\", 2)");
                table.HasCheckConstraint("CK_OrderItems_TotalPrice",
                    "\"TotalPrice\" > 0 AND \"TotalPrice\" <= 79228162514264337593543950335 " +
                    "AND \"TotalPrice\" = trunc(\"TotalPrice\", 2)");
            });

            item.WithOwner()
                .HasForeignKey("OrderId");

            item.Property<int>("Id")
                .ValueGeneratedOnAdd();

            item.HasKey("OrderId", "Id");

            item.Property(x => x.ProductId)
                .IsRequired();

            item.Property(x => x.Name)
                .HasMaxLength(OrderItem.MaxNameLength)
                .IsRequired();

            item.Property(x => x.UnitPrice)
                .HasColumnType("numeric")
                .IsRequired();

            item.Property(x => x.Quantity)
                .IsRequired();

            item.Property(x => x.TotalPrice)
                .HasColumnType("numeric")
                .IsRequired();
        });

        builder.Navigation(order => order.Items)
            .HasField("_items")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
