namespace OrderFleet.Application.Clock;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
