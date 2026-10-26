namespace OrderFleet.Infrastructure.Couriers;

public sealed class CourierLocationEventOptions
{
    public double MinimumDistanceMeters { get; set; } = 25;
    public double MinimumIntervalSeconds { get; set; } = 10;
}
