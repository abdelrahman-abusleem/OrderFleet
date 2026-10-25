using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OrderFleet.Application.Catalog;
using OrderFleet.Application.Clock;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Identity;
using OrderFleet.Domain.Orders;

namespace OrderFleet.Application.Orders;

public sealed class CreateOrder(IOrderingStore store, OrderingPolicy policy, IClock clock)
{
    public async Task<Guid> ExecuteAsync(Actor actor, CreateOrderRequest request, string? key, CancellationToken cancellationToken)
    {
        if (!actor.IsInRole(OrderFleetRoles.Customer)) throw new AccessDeniedException();
        Validate(request, key);
        var fingerprint = Fingerprint(request);
        var replay = await store.FindReplayAsync(actor.Id, key!, cancellationToken);
        if (replay is not null) return Replay(replay, fingerprint);
        var ids = request.Items!.Select(x => x!.ProductId).ToArray();
        var catalog = await store.ReadCatalogAsync(request.RestaurantId, ids, cancellationToken);
        if (!catalog.Active || catalog.Products.Count != ids.Length || catalog.Products.Any(x => !x.IsAvailable))
            throw new RequestValidationException("Restaurant and all requested products must be available.");
        var products = catalog.Products.ToDictionary(x => x.Id);
        var items = request.Items!.Select(x =>
        {
            var product = products[x!.ProductId];
            return new OrderItem(product.Id, product.Name, product.Price, x.Quantity);
        }).ToArray();
        var now = clock.UtcNow;
        Order order;
        try { order = Order.Create(actor.Id, request.RestaurantId, items, policy.Limits, now); }
        catch (ArgumentException) { throw new RequestValidationException("Order exceeds configured pricing limits."); }
        var delivery = new DeliveryDetails(request.Latitude!.Value, request.Longitude!.Value, now.Add(policy.AcceptanceTimeout));
        try { await store.CommitNewAsync(order, delivery, key!, fingerprint, cancellationToken); }
        catch (DuplicateRequestException)
        {
            var committed = await store.FindReplayAsync(actor.Id, key!, cancellationToken)
                ?? throw new OperationConflictException("Request is being processed; retry with the same key.");
            return Replay(committed, fingerprint);
        }
        return order.Id;
    }

    private void Validate(CreateOrderRequest request, string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100 || key.Any(char.IsControl))
            throw new RequestValidationException("An Idempotency-Key of at most 100 characters is required.");
        _ = ManageCatalog.ValidateCoordinates(request.Latitude, request.Longitude);
        if (request.RestaurantId == Guid.Empty || request.Items is null || request.Items.Count == 0
            || request.Items.Count > policy.Limits.MaxItemsPerOrder
            || request.Items.Any(x => x is null || x.ProductId == Guid.Empty || x.Quantity <= 0 || x.Quantity > policy.Limits.MaxQuantityPerItem))
            throw new RequestValidationException("A restaurant and valid items within ordering limits are required.");
        if (request.Items.Select(x => x!.ProductId).Distinct().Count() != request.Items.Count)
            throw new RequestValidationException("Duplicate products are not allowed.");
    }

    private static Guid Replay(OrderReplay replay, string fingerprint) => replay.Fingerprint == fingerprint
        ? replay.OrderId : throw new OperationConflictException("Idempotency-Key was already used for a different request.");

    private static string Fingerprint(CreateOrderRequest request)
    {
        var canonical = $"{request.RestaurantId:N}|{request.Latitude!.Value.ToString("G29", CultureInfo.InvariantCulture)}|" +
            request.Longitude!.Value.ToString("G29", CultureInfo.InvariantCulture) + "|" +
            string.Join(";", request.Items!.OrderBy(x => x!.ProductId).Select(x => $"{x!.ProductId:N}:{x.Quantity}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
