using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class IdentityAdministrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static async Task<Guid> Id(HttpClient client) => (await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetGuid();

    [Fact]
    public async Task AnonymousSelfPromotionAndRevokedOwner_AreDenied()
    {
        await using var factory = new ApiFactory(fixture);
        using var admin = await ClientAsync(factory, "Admin");
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        using var staff = await ClientAsync(factory, "RestaurantStaff");
        using var anonymous = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var actor = await Id(admin);
        var ownerId = await Id(owner);
        var target = await Id(staff);
        var (restaurant, _) = await CatalogAsync(owner);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsync($"/api/v1/identity/users/{target}/roles/Courier", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsync($"/api/v1/identity/users/{actor}/roles/Courier", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsync($"/api/v1/restaurants/{restaurant}/memberships/{ownerId}", null)).StatusCode);
        await using (var context = fixture.CreateContext()) await context.UserRoles.Where(x => x.UserId == ownerId).ExecuteDeleteAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsync($"/api/v1/restaurants/{restaurant}/memberships/{target}", null)).StatusCode);
    }

    [Fact]
    public async Task ConcurrentOwnerMembershipRevocation_IsRecheckedAfterActorLock()
    {
        await using var factory = new ApiFactory(fixture);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        using var delegateOwner = await ClientAsync(factory, "RestaurantOwner");
        using var staff = await ClientAsync(factory, "RestaurantStaff");
        var (restaurant, _) = await CatalogAsync(owner);
        var delegateId = await Id(delegateOwner);
        var target = await Id(staff);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync($"/api/v1/restaurants/{restaurant}/memberships/{delegateId}", null)).StatusCode);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        var pid = await blocker.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\"={delegateId} FOR UPDATE");
        var grant = delegateOwner.PutAsync($"/api/v1/restaurants/{restaurant}/memberships/{target}", null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await using var observer = fixture.CreateContext();
            while (!await observer.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE {pid} = ANY(pg_blocking_pids(pid))) AS "Value"
                """).SingleAsync(timeout.Token))
            {
                Assert.False(grant.IsCompleted);
                await Task.Delay(20, timeout.Token);
            }
            await blocker.RestaurantMemberships.Where(x => x.UserId == delegateId && x.RestaurantId == restaurant).ExecuteDeleteAsync(timeout.Token);
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(HttpStatusCode.NotFound, (await grant.WaitAsync(timeout.Token)).StatusCode);
        }
        finally
        {
            if (blocker.Database.CurrentTransaction is not null) await transaction.RollbackAsync();
            await grant;
        }
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.RestaurantMemberships.AnyAsync(x => x.UserId == target && x.RestaurantId == restaurant));
    }

    [Fact]
    public async Task OwnerCanRemoveStaleMembership_WithoutRestoringRevokedSystemRole()
    {
        await using var factory = new ApiFactory(fixture);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        using var admin = await ClientAsync(factory, "Admin");
        using var staff = await ClientAsync(factory, "RestaurantStaff");
        var (restaurant, _) = await CatalogAsync(owner);
        var target = await Id(staff);
        var route = $"/api/v1/restaurants/{restaurant}/memberships/{target}";
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(route, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/identity/users/{target}/roles/RestaurantStaff")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(route)).StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.RestaurantMemberships.AnyAsync(x => x.UserId == target && x.RestaurantId == restaurant));
    }

    [Fact]
    public async Task LiveAdmin_GrantsAndRevokesAllowedRole_IdempotentlyAndAudited()
    {
        await using var factory = new ApiFactory(fixture);
        using var admin = await ClientAsync(factory, "Admin");
        using var courier = await ClientAsync(factory, "Courier");
        using var target = await ClientAsync(factory, "Customer");
        var targetId = await Id(target);
        var courierId = await Id(courier);
        var route = $"/api/v1/identity/users/{targetId}/roles/Courier";
        var replies = await Task.WhenAll(admin.PutAsync(route, null), admin.PutAsync(route, null));
        Assert.All(replies, reply => Assert.Equal(HttpStatusCode.NoContent, reply.StatusCode));
        Assert.Equal(HttpStatusCode.Forbidden, (await target.PutAsync("/api/v1/couriers/me", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/identity/users/{courierId}/roles/Courier")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await courier.PutAsync("/api/v1/couriers/me", null)).StatusCode);
        await using var verify = fixture.CreateContext();
        var role = await verify.Roles.Where(x => x.Name == "Courier").Select(x => x.Id).SingleAsync();
        Assert.False(await verify.UserRoles.AnyAsync(x => x.UserId == targetId && x.RoleId == role));
        Assert.Equal(1, await verify.OperationAudits.CountAsync(x => x.TargetId == targetId && x.Action == "GrantRole:Courier"));
        Assert.Equal(1, await verify.OperationAudits.CountAsync(x => x.TargetId == targetId && x.Action == "RevokeRole:Courier"));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    [InlineData("Unknown")]
    public async Task ForbiddenSystemRole_CannotBeAdministered(string role)
    {
        await using var factory = new ApiFactory(fixture);
        using var admin = await ClientAsync(factory, "Admin");
        using var target = await ClientAsync(factory, "Customer");
        var id = await Id(target);
        var route = $"/api/v1/identity/users/{id}/roles/{role}";
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync(route, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync(route)).StatusCode);
    }

    [Fact]
    public async Task MembershipManagement_RequiresLiveOwnerScopeAndEligibleTarget()
    {
        await using var factory = new ApiFactory(fixture);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        using var otherOwner = await ClientAsync(factory, "RestaurantOwner");
        using var staff = await ClientAsync(factory, "RestaurantStaff");
        using var customer = await ClientAsync(factory, "Customer");
        var (restaurant, _) = await CatalogAsync(owner);
        var (foreign, _) = await CatalogAsync(otherOwner);
        var staffId = await Id(staff);
        var route = $"/api/v1/restaurants/{restaurant}/memberships/{staffId}";
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PutAsync(route, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherOwner.PutAsync(route, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsync($"/api/v1/restaurants/{foreign}/memberships/{staffId}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsync($"/api/v1/restaurants/{restaurant}/memberships/{await Id(customer)}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(route, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(route, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/v1/orders?restaurantId={restaurant}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/v1/orders?restaurantId={restaurant}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsync($"/api/v1/identity/users/{staffId}/roles/Courier", null)).StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.RestaurantMemberships.AnyAsync(x => x.UserId == staffId && x.RestaurantId == restaurant));
        Assert.Equal(2, await verify.OperationAudits.CountAsync(x => x.TargetId == restaurant && x.Action.StartsWith("Membership")));
    }

    [Fact]
    public async Task ConcurrentAdminRevocation_IsRecheckedAfterActorLock()
    {
        await using var factory = new ApiFactory(fixture);
        using var admin = await ClientAsync(factory, "Admin");
        using var target = await ClientAsync(factory, "Customer");
        var adminId = await Id(admin);
        var targetId = await Id(target);
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        var pid = await blocker.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\"={adminId} FOR UPDATE");
        var grant = admin.PutAsync($"/api/v1/identity/users/{targetId}/roles/Courier", null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await using var observer = fixture.CreateContext();
            while (!await observer.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE {pid} = ANY(pg_blocking_pids(pid))) AS "Value"
                """).SingleAsync(timeout.Token))
            {
                Assert.False(grant.IsCompleted);
                await Task.Delay(20, timeout.Token);
            }
            await blocker.UserRoles.Where(x => x.UserId == adminId).ExecuteDeleteAsync(timeout.Token);
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(HttpStatusCode.Forbidden, (await grant.WaitAsync(timeout.Token)).StatusCode);
        }
        finally
        {
            if (blocker.Database.CurrentTransaction is not null) await transaction.RollbackAsync();
            await grant;
        }
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.UserRoles.CountAsync(x => x.UserId == targetId));
        Assert.False(await verify.OperationAudits.AnyAsync(x => x.TargetId == targetId && x.Action == "GrantRole:Courier"));
    }
}
