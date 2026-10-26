using OrderFleet.Domain.Orders;

namespace OrderFleet.Application.Eta;

public sealed record EtaPolicy(double AverageSpeedKmh, double DistanceFactor, int PreparationMinutes, int HandoffMinutes, int MaximumMinutes);
public sealed record EtaInputs(string Status, double PickupToDestinationMeters, double? CourierToPickupMeters,
    double? CourierToDestinationMeters, DateTimeOffset? LocationObservedAt, DateTimeOffset? AcceptedAt);
public sealed record EtaEstimate(int? Minutes, string Confidence, string Basis, DateTimeOffset CalculatedAt, bool IsApproximate = true);

public sealed class EtaCalculator(EtaPolicy policy, TimeSpan locationFreshness)
{
    public EtaEstimate Calculate(EtaInputs input, DateTimeOffset now)
    {
        if (input.Status == nameof(OrderStatus.Delivered)) return new(0, "Completed", "Delivery completed.", now);
        if (input.Status == nameof(OrderStatus.Cancelled)) return new(null, "Unavailable", "Order cancelled.", now);
        var fresh = input.LocationObservedAt.HasValue && input.LocationObservedAt >= now - locationFreshness
            && input.LocationObservedAt <= now.AddSeconds(30);
        var distance = input.PickupToDestinationMeters;
        var buffer = policy.HandoffMinutes;
        var basis = "Straight-line distance with speed and handoff assumptions.";
        if (input.Status == nameof(OrderStatus.PickedUp) && fresh && input.CourierToDestinationMeters.HasValue)
            distance = input.CourierToDestinationMeters.Value;
        else if (input.Status == nameof(OrderStatus.Assigned) && fresh && input.CourierToPickupMeters.HasValue)
        {
            distance += input.CourierToPickupMeters.Value;
            buffer += RemainingPreparation(input.AcceptedAt, now);
        }
        else
        {
            fresh = false;
            buffer += input.Status == nameof(OrderStatus.PickedUp) ? 5 : policy.PreparationMinutes + 10;
            basis = "Location fallback: pickup-to-destination distance and conservative preparation/dispatch buffer.";
        }
        var minutes = distance / 1000 * policy.DistanceFactor / policy.AverageSpeedKmh * 60 + buffer;
        var bounded = (int)Math.Ceiling(Math.Clamp(minutes, 1, policy.MaximumMinutes));
        return new(bounded, fresh ? "Approximate" : "Low", basis, now);
    }

    private int RemainingPreparation(DateTimeOffset? acceptedAt, DateTimeOffset now) => acceptedAt.HasValue
        ? Math.Max(0, policy.PreparationMinutes - (int)Math.Max(0, (now - acceptedAt.Value).TotalMinutes)) : policy.PreparationMinutes;
}
