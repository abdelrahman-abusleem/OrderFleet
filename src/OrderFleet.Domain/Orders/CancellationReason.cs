
namespace OrderFleet.Domain.Orders;

public enum CancellationReason
{
    CustomerRequested = 1,
    RestaurantRejected = 2,
    AcceptanceTimedOut = 3,
    RestaurantCancelled = 4,
    AdminCancelled = 5
}
