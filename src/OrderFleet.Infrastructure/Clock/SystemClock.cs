using OrderFleet.Application.Clock;

namespace OrderFleet.Infrastructure.Clock;

public sealed class SystemClock(TimeProvider clock) : IClock
{
    public DateTimeOffset UtcNow => clock.GetUtcNow();
}
