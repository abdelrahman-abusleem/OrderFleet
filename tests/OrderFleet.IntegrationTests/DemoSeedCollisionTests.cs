using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using OrderFleet.Api.Development;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class DemoSeedCollisionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ExistingPublicIdentity_CannotGainSeededAdministratorAuthority_AndSeedRollsBack()
    {
        await using var factory = new ApiFactory(fixture);
        using (var scope = factory.Services.CreateScope())
        {
            var user = new IdentityUser<Guid> { Id = Guid.NewGuid(), Email = "admin@demo.orderfleet.test", UserName = "admin@demo.orderfleet.test" };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>()
                .CreateAsync(user, "Aa1!" + Guid.NewGuid().ToString("N"))).Succeeded);
        }
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => DemoBootstrap.ExecuteAsync(factory.Services,
            new HostingEnvironment { EnvironmentName = "Development" }, "Aa1!" + Guid.NewGuid().ToString("N"), CancellationToken.None));
        Assert.Contains("another identity", exception.Message);
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.Users.CountAsync());
        Assert.Empty(await verify.UserRoles.ToArrayAsync());
        Assert.Empty(await verify.Restaurants.ToArrayAsync());
    }
}
