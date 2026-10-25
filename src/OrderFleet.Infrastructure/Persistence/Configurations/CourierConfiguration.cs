using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OrderFleet.Domain.Couriers;
using OrderFleet.Infrastructure.Couriers;

namespace OrderFleet.Infrastructure.Persistence.Configurations;

public sealed class CourierConfiguration : IEntityTypeConfiguration<Courier>
{
    public void Configure(EntityTypeBuilder<Courier> builder)
    {
        builder.ToTable("Couriers", table =>
        {
            table.HasCheckConstraint("CK_Courier_Location", "(\"Latitude\" IS NULL AND \"Longitude\" IS NULL AND \"LocationObservedAt\" IS NULL) OR (\"Latitude\" IS NOT NULL AND \"Longitude\" IS NOT NULL AND \"LocationObservedAt\" IS NOT NULL AND \"Latitude\" BETWEEN -90 AND 90 AND \"Longitude\" BETWEEN -180 AND 180)");
            table.HasCheckConstraint("CK_Courier_Assignment", "(\"Status\" IN ('Offline','Available') AND \"OrderId\" IS NULL AND \"AssignmentId\" IS NULL) OR (\"Status\" IN ('Reserved','Busy') AND \"OrderId\" IS NOT NULL AND \"AssignmentId\" IS NOT NULL)");
        });
        builder.HasKey(x => x.Id);
        builder.HasOne<IdentityUser<Guid>>().WithOne().HasForeignKey<Courier>(x => x.Id);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property<uint>("xmin").IsRowVersion();
        builder.Property(x => x.LocationObservedAt).HasConversion(new ValueConverter<DateTimeOffset, DateTimeOffset>(x => x.ToUniversalTime(), x => x));
        builder.HasIndex(x => x.OrderId).IsUnique().HasFilter("\"OrderId\" IS NOT NULL");
        builder.HasIndex(x => x.AssignmentId).IsUnique().HasFilter("\"AssignmentId\" IS NOT NULL");
    }
}

public sealed class CourierPositionConfiguration : IEntityTypeConfiguration<CourierPosition>
{
    public void Configure(EntityTypeBuilder<CourierPosition> builder)
    {
        builder.ToTable("CourierPositions");
        builder.HasKey(x => x.CourierId);
        builder.HasOne<Courier>().WithOne().HasForeignKey<CourierPosition>(x => x.CourierId);
        builder.Property(x => x.Location).HasColumnType("geography(Point,4326)").IsRequired();
        builder.HasIndex(x => x.Location).HasMethod("gist");
        builder.HasIndex(x => x.ObservedAt);
    }
}
