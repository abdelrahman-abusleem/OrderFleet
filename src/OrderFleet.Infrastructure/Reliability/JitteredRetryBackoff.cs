namespace OrderFleet.Infrastructure.Reliability;

public interface IRetryBackoff
{
    TimeSpan GetDelay(int attempt, TimeSpan initial, TimeSpan maximum);
}

public sealed class JitteredRetryBackoff(Random? random = null) : IRetryBackoff
{
    public TimeSpan GetDelay(int attempt, TimeSpan initial, TimeSpan maximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        if (initial <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initial));
        if (maximum < initial) throw new ArgumentOutOfRangeException(nameof(maximum));
        var exponential = initial.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 30));
        var jitter = 1 + (random ?? Random.Shared).NextDouble() * 0.2;
        return TimeSpan.FromMilliseconds(Math.Min(maximum.TotalMilliseconds, exponential * jitter));
    }
}
