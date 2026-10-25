using OrderFleet.Application.Orders;

namespace OrderFleet.Infrastructure.Orders;

public sealed class OrderDeliveryInformation
{
    public Guid OrderId { get; private set; }
    public decimal Latitude { get; private set; }
    public decimal Longitude { get; private set; }
    public DateTimeOffset AcceptanceDeadline { get; private set; }

    private OrderDeliveryInformation() { }
    public OrderDeliveryInformation(Guid orderId, DeliveryDetails details)
    {
        OrderId = orderId;
        Latitude = details.Latitude;
        Longitude = details.Longitude;
        AcceptanceDeadline = details.AcceptanceDeadline.ToUniversalTime();
    }
}

public sealed class OrderCreationRequest
{
    public Guid ActorId { get; private set; }
    public string Key { get; private set; } = "";
    public string Fingerprint { get; private set; } = "";
    public Guid OrderId { get; private set; }

    private OrderCreationRequest() { }
    public OrderCreationRequest(Guid actorId, string key, string fingerprint, Guid orderId)
    {
        ActorId = actorId;
        Key = key;
        Fingerprint = fingerprint;
        OrderId = orderId;
    }
}
