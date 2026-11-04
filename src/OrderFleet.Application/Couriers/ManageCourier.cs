using OrderFleet.Application.Catalog;
using OrderFleet.Application.Clock;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Identity;
using OrderFleet.Domain.Couriers;

namespace OrderFleet.Application.Couriers;

public sealed class ManageCourier(ICourierStore store, IClock clock, CourierPolicy policy)
{
    public async Task RegisterAsync(Actor actor, CancellationToken cancellationToken)
    {
        RequireCourier(actor);
        if (await store.FindAsync(actor.Id, cancellationToken) is not null) return;
        var courier = Courier.Register(actor.Id);
        store.Add(courier);
        await store.SaveAsync(courier, cancellationToken);
    }

    public async Task<CourierView> GetAsync(Actor actor, CancellationToken cancellationToken)
    {
        var courier = await OwnAsync(actor, cancellationToken);
        return new(courier.Id, courier.Status.ToString(), courier.LocationObservedAt);
    }

    public async Task SetAvailabilityAsync(Actor actor, bool available, CancellationToken cancellationToken)
    {
        var courier = await OwnAsync(actor, cancellationToken);
        courier.SetAvailable(available);
        await store.SaveAsync(courier, cancellationToken);
    }

    public async Task UpdateLocationAsync(Actor actor, decimal? latitude, decimal? longitude,
        DateTimeOffset? observedAt, CancellationToken cancellationToken)
    {
        var courier = await OwnAsync(actor, cancellationToken);
        var coordinates = ManageCatalog.ValidateCoordinates(latitude, longitude);
        var now = clock.UtcNow;
        if (observedAt is null || observedAt < now - policy.LocationFreshness || observedAt > now + policy.FutureSkew)
            throw new RequestValidationException("Location observation is missing or outside the allowed time window.");


        var utc = observedAt.Value.ToUniversalTime();
        var observation = new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
        var previousObservation = courier.LocationObservedAt;
        courier.UpdateLocation(coordinates, observation);
        if (previousObservation == observation) return;
        await store.SaveAsync(courier, cancellationToken);
    }

    private async Task<Courier> OwnAsync(Actor actor, CancellationToken cancellationToken)
    {
        RequireCourier(actor);
        return await store.FindAsync(actor.Id, cancellationToken) ?? throw new ResourceNotFoundException();
    }
    private static void RequireCourier(Actor actor)
    {
        if (!actor.IsInRole(OrderFleetRoles.Courier)) throw new AccessDeniedException();
    }
}
