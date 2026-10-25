using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderFleet.Domain.Restaurants;
using OrderFleet.Domain.Orders;
using OrderFleet.Infrastructure.Orders;

namespace OrderFleet.Infrastructure.Persistence.Configurations;

public sealed class RestaurantConfiguration : IEntityTypeConfiguration<Restaurant>
{
    public void Configure(EntityTypeBuilder<Restaurant> builder)
    {
        builder.ToTable("Restaurants", table =>
        {
            table.HasCheckConstraint("CK_Restaurant_Latitude", "\"Latitude\" BETWEEN -90 AND 90");
            table.HasCheckConstraint("CK_Restaurant_Longitude", "\"Longitude\" BETWEEN -180 AND 180");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Latitude).HasColumnType("numeric");
        builder.Property(x => x.Longitude).HasColumnType("numeric");
        builder.Property<uint>("xmin").IsRowVersion();
    }
}

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products", table => table.HasCheckConstraint("CK_Products_Price",
            "\"Price\" > 0 AND \"Price\" <= 79228162514264337593543950335 AND \"Price\" = trunc(\"Price\", 2)"));
        builder.HasKey(x => x.Id);
        builder.HasOne<Restaurant>().WithMany().HasForeignKey(x => x.RestaurantId);
        builder.HasIndex(x => x.RestaurantId);
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.Price).HasColumnType("numeric");
        builder.Property<uint>("xmin").IsRowVersion();
    }
}

public sealed class DeliveryConfiguration : IEntityTypeConfiguration<OrderDeliveryInformation>
{
    public void Configure(EntityTypeBuilder<OrderDeliveryInformation> builder)
    {
        builder.ToTable("OrderDeliveryInformation", table =>
        {
            table.HasCheckConstraint("CK_Delivery_Latitude", "\"Latitude\" BETWEEN -90 AND 90");
            table.HasCheckConstraint("CK_Delivery_Longitude", "\"Longitude\" BETWEEN -180 AND 180");
        });
        builder.HasKey(x => x.OrderId);
        builder.HasOne<Order>().WithOne().HasForeignKey<OrderDeliveryInformation>(x => x.OrderId);
        builder.Property(x => x.Latitude).HasColumnType("numeric");
        builder.Property(x => x.Longitude).HasColumnType("numeric");
        builder.HasIndex(x => x.AcceptanceDeadline);
    }
}

public sealed class OrderCreationRequestConfiguration : IEntityTypeConfiguration<OrderCreationRequest>
{
    public void Configure(EntityTypeBuilder<OrderCreationRequest> builder)
    {
        builder.ToTable("OrderCreationRequests");
        builder.HasKey(x => new { x.ActorId, x.Key });
        builder.Property(x => x.Key).HasMaxLength(100);
        builder.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
        builder.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId);
    }
}

public sealed class OrderAuditConfiguration : IEntityTypeConfiguration<OrderAuditEntry>
{
    public void Configure(EntityTypeBuilder<OrderAuditEntry> builder)
    {
        builder.ToTable("OrderAuditEntries");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Action).HasMaxLength(40).IsRequired();
        builder.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId);
        builder.HasIndex(x => new { x.OrderId, x.OccurredAt });
    }
}
