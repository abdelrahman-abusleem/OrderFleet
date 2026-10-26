using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using OrderFleet.Application.Identity;
using OrderFleet.Infrastructure.Persistence;

namespace OrderFleet.IntegrationTests;

public sealed class RoleSeedIntegrityTests
{
    [Fact]
    public void EveryRole_HasItsStableSeedIdentityRegardlessOfEnumerationOrder()
    {
        var expected = new Dictionary<string, Guid>
        {
            [OrderFleetRoles.Customer] = new("c92cb45d-1e41-43c8-b5dc-000000000001"),
            [OrderFleetRoles.RestaurantStaff] = new("c92cb45d-1e41-43c8-b5dc-000000000002"),
            [OrderFleetRoles.RestaurantOwner] = new("c92cb45d-1e41-43c8-b5dc-000000000003"),
            [OrderFleetRoles.Courier] = new("c92cb45d-1e41-43c8-b5dc-000000000004"),
            [OrderFleetRoles.Admin] = new("c92cb45d-1e41-43c8-b5dc-000000000005")
        };
        using var context = new OrderFleetDbContext(new DbContextOptionsBuilder<OrderFleetDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=unused", x => x.UseNetTopologySuite()).Options);
        var seed = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(IdentityRole<Guid>))!.GetSeedData().ToArray();
        Assert.Equal(expected.Count, OrderFleetRoles.All.Count);
        Assert.Equal(expected.Count, seed.Length);
        foreach (var role in OrderFleetRoles.All.Reverse())
        {
            Assert.True(expected.TryGetValue(role, out var id), $"Role {role} requires an explicit stable seed id.");
            var row = Assert.Single(seed, x => Equals(x["Name"], role));
            Assert.Equal(id, row["Id"]);
            Assert.Equal(role.ToUpperInvariant(), row["NormalizedName"]);
            Assert.Equal(id.ToString(), row["ConcurrencyStamp"]);
        }
    }
}
