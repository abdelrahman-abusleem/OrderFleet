using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OrderFleet.Domain.Orders;
using OrderFleet.Domain.Restaurants;
using OrderFleet.Infrastructure.Identity;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class IdentityApiTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ConcurrentRegistration_CreatesOneCustomerAccount()
    {
        await using var factory = new ApiFactory(fixture);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var email = $"{Guid.NewGuid():N}@example.test";
        var password = $"Aa1!{Guid.NewGuid():N}";
        var responses = await Task.WhenAll(
            client.PostAsJsonAsync("/api/v1/auth/register", new { email, password }),
            client.PostAsJsonAsync("/api/v1/auth/register", new { email, password }));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        await using var context = fixture.CreateContext();
        var user = await context.Users.SingleAsync(x => x.NormalizedEmail == email.ToUpperInvariant());
        var grant = await context.UserRoles.SingleAsync(x => x.UserId == user.Id);
        Assert.Equal("Customer", (await context.Roles.SingleAsync(x => x.Id == grant.RoleId)).Name);
    }

    [Fact]
    public async Task FailedLogins_LockAccount_AndCredentialRequestsAreRateLimited()
    {
        await using var factory = new ApiFactory(fixture);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var (_, email, password) = await RegisterAndLoginAsync(client);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login",
                new { email, password = "WrongPassword1!" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password })).StatusCode);
        await using (var context = fixture.CreateContext())
        {
            var user = await context.Users.SingleAsync(x => x.NormalizedEmail == email.ToUpperInvariant());
            Assert.NotNull(user.LockoutEnd);
            Assert.True(user.LockoutEnd > DateTimeOffset.UtcNow);
        }
        HttpResponseMessage response = null!;
        for (var i = 0; i < 4; i++)
            response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task RevokedRestaurantMembership_DeniesExistingTokenImmediately()
    {
        await using var factory = new ApiFactory(fixture);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var user = Guid.NewGuid();
        var restaurantEntity = Restaurant.Create("Identity test kitchen", new(24.7m, 46.7m));
        var restaurant = restaurantEntity.Id;
        var order = Order.Create(Guid.NewGuid(), restaurant,
            [new(Guid.NewGuid(), "Tea", 3m, 2)], new(99, 20, 9999.99m, 99999.99m), DateTimeOffset.UtcNow);
        await using (var context = fixture.CreateContext())
        {
            context.Restaurants.Add(restaurantEntity);
            context.Users.Add(new IdentityUser<Guid> { Id = user, UserName = $"{user:N}@example.test" });
            context.RestaurantMemberships.Add(new RestaurantMembership(user, restaurant));
            context.Orders.Add(order);
            await context.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(factory, user, "RestaurantStaff"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/orders/{order.Id}")).StatusCode);
        await using (var context = fixture.CreateContext())
        {
            var membership = await context.RestaurantMemberships.SingleAsync(x => x.UserId == user);
            context.RestaurantMemberships.Remove(membership);
            await context.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/orders/{order.Id}")).StatusCode);
    }

    [Fact]
    public async Task RegisterAndLogin_HashesPassword_AndIssuesCustomerToken()
    {
        await using var factory = new ApiFactory(fixture);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var (id, email, password) = await RegisterAndLoginAsync(client);
        var response = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Contains("Customer", body.GetProperty("roles").EnumerateArray().Select(x => x.GetString()));
        await using var context = fixture.CreateContext();
        var stored = await context.Users.SingleAsync(x => x.Id == id);
        Assert.NotEqual(password, stored.PasswordHash);
        Assert.False(string.IsNullOrWhiteSpace(stored.PasswordHash));
        Assert.Equal(email.ToUpperInvariant(), stored.NormalizedEmail);
        Assert.DoesNotContain(password, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Register_RejectsWeakPasswordAndRoleInjection_AndHidesDuplicates()
    {
        await using var factory = new ApiFactory(fixture);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var email = $"{Guid.NewGuid():N}@example.test";
        var password = $"Aa1!{Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password, role = "Admin" })).StatusCode);
        var first = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password });
        var repeated = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(first.StatusCode, repeated.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await repeated.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, password = "short" })).StatusCode);
        await using var context = fixture.CreateContext();
        Assert.Equal(1, await context.Users.CountAsync(x => x.NormalizedEmail == email.ToUpperInvariant()));
    }

    [Fact]
    public async Task Login_UsesSameErrorForUnknownUserAndWrongPassword()
    {
        await using var factory = new ApiFactory(fixture);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var (_, email, _) = await RegisterAndLoginAsync(client);
        var unknown = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = $"{Guid.NewGuid():N}@example.test", password = "WrongPassword1!" });
        var wrong = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password = "WrongPassword1!" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(unknown.StatusCode, wrong.StatusCode);
        var unknownJson = await unknown.Content.ReadFromJsonAsync<JsonElement>();
        var wrongJson = await wrong.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(unknownJson.GetProperty("title").GetString(), wrongJson.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("subject")]
    public async Task Jwt_RejectsInvalidSignatureIssuerAudienceLifetimeAndSubject(string defect)
    {
        await using var factory = new ApiFactory(fixture);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(factory, Guid.NewGuid(), "Customer", defect));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task MissingAuthenticationAndWrongRole_AreDenied()
    {
        await using var factory = new ApiFactory(fixture);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(factory, Guid.NewGuid(), "Visitor"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/orders/{Guid.NewGuid()}")).StatusCode);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("RestaurantStaff")]
    [InlineData("RestaurantOwner")]
    [InlineData("Courier")]
    public async Task OrderReads_RequireMatchingOwnershipOrCurrentAssignment(string role)
    {
        await using var factory = new ApiFactory(fixture);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var user = Guid.NewGuid();
        var restaurantEntity = Restaurant.Create("Identity test kitchen", new(24.7m, 46.7m));
        var restaurant = restaurantEntity.Id;
        var order = Order.Create(role == "Customer" ? user : Guid.NewGuid(), restaurant,
            [new(Guid.NewGuid(), "Tea", 3m, 2)], new(99, 20, 9999.99m, 99999.99m), DateTimeOffset.UtcNow);
        if (role == "Courier")
        {
            order.Accept(DateTimeOffset.UtcNow);
            order.Assign(user, DateTimeOffset.UtcNow);
        }
        await using (var context = fixture.CreateContext())
        {
            context.Restaurants.Add(restaurantEntity);
            if (role.StartsWith("Restaurant", StringComparison.Ordinal))
            {
                context.Users.Add(new IdentityUser<Guid> { Id = user, UserName = $"{user:N}@example.test" });
                context.RestaurantMemberships.Add(new RestaurantMembership(user, restaurant));
            }
            context.Orders.Add(order);
            await context.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(factory, user, role));
        var allowed = await client.GetAsync($"/api/v1/orders/{order.Id}");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(order.Id, (await allowed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token(factory, Guid.NewGuid(), role));
        var denied = await client.GetAsync($"/api/v1/orders/{order.Id}");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        var missing = await client.GetAsync($"/api/v1/orders/{Guid.NewGuid()}");
        Assert.Equal(denied.StatusCode, missing.StatusCode);
    }

    private static async Task<(Guid Id, string Email, string Password)> RegisterAndLoginAsync(HttpClient client)
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        var password = $"Aa1!{Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password })).StatusCode);
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(ApiFactory.Issuer, jwt.Issuer);
        Assert.Contains(ApiFactory.Audience, jwt.Audiences);
        return (Guid.Parse(jwt.Subject), email, password);
    }

    private static string Token(ApiFactory factory, Guid user, string role, string? defect = null)
    {
        var key = defect == "signature" ? new string('x', 64) : factory.SigningKey;
        var now = DateTime.UtcNow;
        var claims = new[] { new Claim("sub", defect == "subject" ? "invalid" : user.ToString()), new Claim("role", role) };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: defect == "issuer" ? "wrong" : ApiFactory.Issuer,
            audience: defect == "audience" ? "wrong" : ApiFactory.Audience,
            claims: claims,
            notBefore: defect == "expired" ? now.AddHours(-2) : defect == "future" ? now.AddHours(1) : now.AddMinutes(-1),
            expires: defect == "expired" ? now.AddHours(-1) : now.AddHours(2),
            signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)));
    }
}
