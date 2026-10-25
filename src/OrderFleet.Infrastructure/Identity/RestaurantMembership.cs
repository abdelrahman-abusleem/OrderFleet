namespace OrderFleet.Infrastructure.Identity;

public sealed class RestaurantMembership
{
    public Guid UserId { get; private set; }
    public Guid RestaurantId { get; private set; }

    private RestaurantMembership() { }

    public RestaurantMembership(Guid userId, Guid restaurantId)
    {
        if (userId == Guid.Empty) throw new ArgumentException("User is required.", nameof(userId));
        if (restaurantId == Guid.Empty) throw new ArgumentException("Restaurant is required.", nameof(restaurantId));
        UserId = userId;
        RestaurantId = restaurantId;
    }
}
