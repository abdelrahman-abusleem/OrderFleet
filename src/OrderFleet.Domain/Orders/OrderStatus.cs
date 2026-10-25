namespace OrderFleet.Domain.Orders;

public enum OrderStatus
{
    Created = 1,
    Accepted = 2,
    Assigned = 3,
    PickedUp = 4,
    Delivered = 5,
    Cancelled = 6
}
