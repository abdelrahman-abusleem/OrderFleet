using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderFleet.Application.Identity;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Orders;
using OrderFleet.Domain.Orders;
using OrderFleet.Infrastructure.Persistence;
using OrderFleet.Application.Payments;
using OrderFleet.Domain.Payments;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class CatalogOrderingApiTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task OpenApi_DescribesBearerAuthenticationAndRequiredIdempotencyHeader()
    {
        await using var factory = new ApiFactory(fixture);
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        Assert.Equal("bearer", document.GetProperty("components").GetProperty("securitySchemes")
            .GetProperty("Bearer").GetProperty("scheme").GetString());
        var create = document.GetProperty("paths").GetProperty("/api/v1/orders").GetProperty("post");
        Assert.Contains(create.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key"
                && parameter.GetProperty("required").GetBoolean());
        Assert.True(create.GetProperty("responses").TryGetProperty("409", out _));
    }

    [Fact]
    public async Task InvalidCreateAndForeignCatalogWrites_AreRejectedWithoutBusinessEffects()
    {
        await using var factory = new ApiFactory(fixture);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var otherOwner = await ClientAsync(factory, "RestaurantOwner");
        using var customer = await ClientAsync(factory, "Customer");
        Assert.Equal(HttpStatusCode.NotFound, (await otherOwner.PutAsJsonAsync(
            $"/api/v1/restaurants/{restaurant}/products/{product}",
            new { name = "Stolen", price = 1m, isAvailable = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync(
            $"/api/v1/restaurants/{restaurant}/products", new { name = "Injected", price = 1m })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync("/api/v1/orders", Request(restaurant, product))).StatusCode);
        foreach (var quantity in new[] { 0, -1, 100 })
            Assert.Equal(HttpStatusCode.BadRequest,
                (await CreateAsync(customer, Request(restaurant, product, quantity), Guid.NewGuid().ToString("N"))).StatusCode);
        var duplicate = new { restaurantId = restaurant, items = new[]
            { new { productId = product, quantity = 1 }, new { productId = product, quantity = 1 } }, latitude = 24.71m, longitude = 46.7m };
        Assert.Equal(HttpStatusCode.BadRequest,
            (await CreateAsync(customer, duplicate, Guid.NewGuid().ToString("N"))).StatusCode);
        var nullItem = new { restaurantId = restaurant, items = new object?[] { null }, latitude = 24.71m, longitude = 46.7m };
        Assert.Equal(HttpStatusCode.BadRequest,
            (await CreateAsync(customer, nullItem, Guid.NewGuid().ToString("N"))).StatusCode);
        using var publicClient = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await publicClient.GetAsync("/api/v1/restaurants")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await publicClient.GetAsync("/api/v1/restaurants?pageSize=51")).StatusCode);
        await using var context = fixture.CreateContext();
        Assert.False(await context.Orders.AnyAsync(x => x.RestaurantId == restaurant));
        var unchanged = await context.Products.SingleAsync(x => x.Id == product);
        Assert.Equal("Tea", unchanged.Name);
        Assert.Equal(3.25m, unchanged.Price);
        Assert.True(unchanged.IsAvailable);
    }

    [Fact]
    public async Task AcceptAndCancel_RespectRolesAndState_AndWriteAtomicAudit()
    {
        await using var factory = new ApiFactory(fixture);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        var created = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsync($"/api/v1/orders/{id}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/orders/{id}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/v1/orders/{id}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await customer.PostAsync($"/api/v1/orders/{id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync($"/api/v1/orders/{id}/cancel", null)).StatusCode);
        await using var context = fixture.CreateContext();
        var order = await context.Orders.SingleAsync(x => x.Id == id);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(CancellationReason.RestaurantCancelled, order.CancelReason);
        Assert.Equal(3, await context.OrderAuditEntries.CountAsync(x => x.OrderId == id));
        var own = await customer.GetFromJsonAsync<JsonElement>("/api/v1/orders");
        Assert.Contains(own.EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);
        var restaurantOrders = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/orders?restaurantId={restaurant}");
        Assert.Contains(restaurantOrders.EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);
    }

    [Theory]
    [InlineData("Customer", "cancel", CancellationReason.CustomerRequested)]
    [InlineData("RestaurantOwner", "reject", CancellationReason.RestaurantRejected)]
    [InlineData("Admin", "cancel", CancellationReason.AdminCancelled)]
    public async Task CancelOrReject_DerivesReasonFromTrustedActor_AndIsTerminal(
        string role, string operation, CancellationReason expectedReason)
    {
        await using var factory = new ApiFactory(fixture);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        using var admin = await ClientAsync(factory, "Admin");
        var created = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var actor = role == "Customer" ? customer : role == "Admin" ? admin : owner;
        Assert.Equal(HttpStatusCode.NoContent, (await actor.PostAsync($"/api/v1/orders/{id}/{operation}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await actor.PostAsync($"/api/v1/orders/{id}/{operation}", null)).StatusCode);
        await using var context = fixture.CreateContext();
        var order = await context.Orders.SingleAsync(x => x.Id == id);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(expectedReason, order.CancelReason);
        Assert.Equal(2, await context.OrderAuditEntries.CountAsync(x => x.OrderId == id));
    }

    [Fact]
    public async Task ConcurrentAcceptAndCustomerCancel_PersistOneLegalWinner()
    {
        await using var factory = new ApiFactory(fixture);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        var created = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var responses = await Task.WhenAll(owner.PostAsync($"/api/v1/orders/{id}/accept", null),
            customer.PostAsync($"/api/v1/orders/{id}/cancel", null));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        await using var context = fixture.CreateContext();
        var order = await context.Orders.SingleAsync(x => x.Id == id);
        Assert.Equal(responses[0].StatusCode == HttpStatusCode.NoContent ? OrderStatus.Accepted : OrderStatus.Cancelled, order.Status);
        Assert.Equal(2, await context.OrderAuditEntries.CountAsync(x => x.OrderId == id));
    }

    [Theory]
    [InlineData("price")]
    [InlineData("product-availability")]
    [InlineData("restaurant-availability")]
    public async Task CatalogChangesBetweenPricingAndCommit_RejectStaleSnapshotWithoutOrphans(string change)
    {
        await using var factory = new ApiFactory(fixture);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        await using var creating = fixture.CreateContext();
        var store = new EfOrderingStore(creating);
        var snapshot = await store.ReadCatalogAsync(restaurant, [product], CancellationToken.None);
        var priced = Assert.Single(snapshot.Products);
        var now = DateTimeOffset.UtcNow;
        var order = Order.Create(Guid.NewGuid(), restaurant, [new(product, priced.Name, priced.Price, 2)],
            new(99, 20, 9999.99m, 99999.99m), now);
        var requestKey = Guid.NewGuid().ToString("N");
        var fingerprint = new string('A', 64);
        using var scope = factory.Services.CreateScope();
        var authorization = await scope.ServiceProvider.GetRequiredService<IPaymentProvider>()
            .AuthorizeAsync(order.CustomerId, requestKey, fingerprint, order.TotalAmount, order.Currency, CancellationToken.None);
        var payment = Payment.Authorize(order.Id, authorization.Id, order.TotalAmount, order.Currency, now);
        await using (var changing = fixture.CreateContext())
        {
            if (change == "restaurant-availability")
                (await changing.Restaurants.SingleAsync(x => x.Id == restaurant)).SetActive(false);
            else
                (await changing.Products.SingleAsync(x => x.Id == product)).Update("Tea", change == "price" ? 99m : 3.25m,
                    change != "product-availability");
            await changing.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<OperationConflictException>(() => store.CommitNewAsync(order, payment,
            new DeliveryDetails(24.71m, 46.7m, now.AddMinutes(5)), requestKey, fingerprint, CancellationToken.None));
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.Orders.AnyAsync(x => x.Id == order.Id));
        Assert.False(await verify.OrderDeliveryInformation.AnyAsync(x => x.OrderId == order.Id));
        Assert.False(await verify.OrderCreationRequests.AnyAsync(x => x.OrderId == order.Id));
        Assert.False(await verify.OrderAuditEntries.AnyAsync(x => x.OrderId == order.Id));
        Assert.False(await verify.Payments.AnyAsync(x => x.OrderId == order.Id));
        Assert.False(await verify.OutboxMessages.AnyAsync(x => x.AggregateId == order.Id));
    }

    [Fact]
    public async Task PersistedDeadline_SurvivesHostRestartAndConfigurationChange()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        string key;
        AuthenticationHeaderValue ownerToken;
        Guid id;
        await using (var first = new ApiFactory(fixture, clock))
        {
            key = first.SigningKey;
            using var owner = await ClientAsync(first, "RestaurantOwner");
            ownerToken = owner.DefaultRequestHeaders.Authorization!;
            var (restaurant, product) = await CatalogAsync(owner);
            using var customer = await ClientAsync(first, "Customer");
            var created = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
            id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }
        clock.Advance(TimeSpan.FromMinutes(5));
        await using var restarted = new ApiFactory(fixture, clock, key);
        restarted.Overrides["Ordering:AcceptanceTimeoutMinutes"] = "60";
        using var client = restarted.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Authorization = ownerToken;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/orders/{id}/accept", null)).StatusCode);
        await using var context = fixture.CreateContext();
        Assert.Equal(OrderStatus.Created, (await context.Orders.SingleAsync(x => x.Id == id)).Status);
    }

    [Fact]
    public async Task Create_UsesCatalogSnapshot_AndKeepsDestinationPrivate()
    {
        await using var factory = new ApiFactory(fixture);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        var request = Request(restaurant, product);
        var response = await CreateAsync(customer, request, Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        var details = await customer.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}");
        Assert.Equal(6.50m, details.GetProperty("totalAmount").GetDecimal());
        var item = details.GetProperty("items")[0];
        Assert.Equal(3.25m, item.GetProperty("unitPrice").GetDecimal());
        Assert.Equal("Tea", item.GetProperty("name").GetString());
        Assert.DoesNotContain("latitude", details.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("longitude", details.ToString(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsJsonAsync(
            $"/api/v1/restaurants/{restaurant}/products/{product}",
            new { name = "New tea", price = 99m, isAvailable = true })).StatusCode);
        var unchanged = await customer.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}");
        Assert.Equal(6.50m, unchanged.GetProperty("totalAmount").GetDecimal());
        Assert.Equal("Tea", unchanged.GetProperty("items")[0].GetProperty("name").GetString());
        var delivery = await customer.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}/delivery");
        Assert.Equal(24.71m, delivery.GetProperty("latitude").GetDecimal());
        using var unrelated = await ClientAsync(factory, "Customer");
        Assert.Equal(HttpStatusCode.NotFound,
            (await unrelated.GetAsync($"/api/v1/orders/{id}/delivery")).StatusCode);
    }

    [Fact]
    public async Task Idempotency_ReplaysSameOutcome_RejectsChangedRequest_AndProtectsConcurrentCreate()
    {
        await using var factory = new ApiFactory(fixture);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        var key = Guid.NewGuid().ToString("N");
        var request = Request(restaurant, product);
        var responses = await Task.WhenAll(CreateAsync(customer, request, key), CreateAsync(customer, request, key));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var first = await responses[0].Content.ReadFromJsonAsync<JsonElement>();
        var second = await responses[1].Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        var replay = await CreateAsync(customer, request, key);
        Assert.Equal(first.GetProperty("id").GetGuid(),
            (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, (await CreateAsync(customer,
            Request(restaurant, product, quantity: 3), key)).StatusCode);
        await using var context = fixture.CreateContext();
        Assert.Equal(1, await context.Orders.CountAsync(x => x.RestaurantId == restaurant));
    }

    [Theory]
    [InlineData(null, 46.7)]
    [InlineData(24.7, null)]
    [InlineData(91.0, 46.7)]
    [InlineData(24.7, -181.0)]
    public async Task Create_RejectsMissingOrInvalidDestination(double? latitude, double? longitude)
    {
        await using var factory = new ApiFactory(fixture);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        var request = new { restaurantId = restaurant, items = new[] { new { productId = product, quantity = 1 } }, latitude, longitude };
        Assert.Equal(HttpStatusCode.BadRequest,
            (await CreateAsync(customer, request, Guid.NewGuid().ToString("N"))).StatusCode);
        await using var context = fixture.CreateContext();
        Assert.False(await context.Orders.AnyAsync(x => x.RestaurantId == restaurant));
    }

    [Fact]
    public async Task Create_RejectsClientPricesForeignProductsAndUnavailableCatalog()
    {
        await using var factory = new ApiFactory(fixture);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        var (otherRestaurant, otherProduct) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        var injected = new { restaurantId = restaurant, items = new[] { new { productId = product, quantity = 1, unitPrice = 0.01m } }, latitude = 24.71m, longitude = 46.7m };
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(customer, injected, Guid.NewGuid().ToString("N"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await CreateAsync(customer, Request(restaurant, otherProduct), Guid.NewGuid().ToString("N"))).StatusCode);
        await owner.PutAsJsonAsync($"/api/v1/restaurants/{restaurant}/products/{product}",
            new { name = "Tea", price = 3.25m, isAvailable = false });
        Assert.Equal(HttpStatusCode.BadRequest,
            (await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"))).StatusCode);
        await owner.PutAsJsonAsync($"/api/v1/restaurants/{otherRestaurant}/availability", new { isActive = false });
        Assert.Equal(HttpStatusCode.BadRequest,
            (await CreateAsync(customer, Request(otherRestaurant, otherProduct), Guid.NewGuid().ToString("N"))).StatusCode);
    }

    [Fact]
    public async Task Accept_EnforcesPersistedDeadlineBeforeWorkerRuns_AndRestaurantOwnership()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        await using var factory = new ApiFactory(fixture, clock);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogAsync(owner);
        using var customer = await ClientAsync(factory, "Customer");
        var created = await CreateAsync(customer, Request(restaurant, product), Guid.NewGuid().ToString("N"));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var unrelated = await ClientAsync(factory, "RestaurantOwner");
        Assert.Equal(HttpStatusCode.NotFound, (await unrelated.PostAsync($"/api/v1/orders/{id}/accept", null)).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/v1/orders/{id}/accept", null)).StatusCode);
        var unchanged = await customer.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{id}");
        Assert.Equal("Created", unchanged.GetProperty("status").GetString());
    }

    internal static object Request(Guid restaurant, Guid product, int quantity = 2) => new
    {
        restaurantId = restaurant, items = new[] { new { productId = product, quantity } },
        latitude = 24.71m, longitude = 46.70m
    };

    internal static async Task<HttpResponseMessage> CreateAsync(HttpClient client, object request, string key)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = JsonContent.Create(request) };
        message.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(message);
    }

    internal static async Task<(Guid Restaurant, Guid Product)> CatalogAsync(HttpClient owner)
    {
        var restaurant = await owner.PostAsJsonAsync("/api/v1/restaurants", new { name = "Kitchen", latitude = 24.7m, longitude = 46.7m });
        Assert.Equal(HttpStatusCode.Created, restaurant.StatusCode);
        var id = (await restaurant.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var product = await owner.PostAsJsonAsync($"/api/v1/restaurants/{id}/products", new { name = "Tea", price = 3.25m, isAvailable = true });
        Assert.Equal(HttpStatusCode.Created, product.StatusCode);
        return (id, (await product.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    internal static async Task<HttpClient> ClientAsync(ApiFactory factory, string role)
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        var password = $"Aa1!{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
            var user = new IdentityUser<Guid> { Id = Guid.NewGuid(), Email = email, UserName = email };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        }
        var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        return client;
    }
}

internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset current = now;
    public override DateTimeOffset GetUtcNow() => current;
    internal void Advance(TimeSpan duration) => current += duration;
}
