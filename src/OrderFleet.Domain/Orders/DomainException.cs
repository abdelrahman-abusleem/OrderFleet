namespace OrderFleet.Domain.Orders;

public sealed class DomainException(string message) : Exception(message);
