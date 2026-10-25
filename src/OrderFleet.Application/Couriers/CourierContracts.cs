using OrderFleet.Domain.Couriers;

namespace OrderFleet.Application.Couriers;

public sealed record CourierView(Guid Id, string Status, DateTimeOffset? LocationObservedAt);
public sealed record NearbyCourier(Guid Id, double DistanceMeters, DateTimeOffset ObservedAt);
public sealed record CourierPolicy(TimeSpan LocationFreshness, TimeSpan FutureSkew, double MaxRadiusMeters);

public interface ICourierStore
{
    Task<Courier?> FindAsync(Guid id, CancellationToken cancellationToken);
    void Add(Courier courier);
    Task SaveAsync(Courier courier, CancellationToken cancellationToken);
    Task<IReadOnlyList<NearbyCourier>> NearbyAsync(decimal latitude, decimal longitude, double radiusMeters,
        DateTimeOffset oldestObservation, DateTimeOffset latestObservation, int limit, CancellationToken cancellationToken);
}
