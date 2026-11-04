namespace OrderFleet.Api.Identity;

public sealed class RequestLimitOptions
{
    public int ReadPerMinute { get; set; } = 120;
    public int WritePerMinute { get; set; } = 60;
    public int CreatePerMinute { get; set; } = 10;
    public int HubPerMinute { get; set; } = 60;

    public bool IsValid() => new[] { ReadPerMinute, WritePerMinute, CreatePerMinute, HubPerMinute }
        .All(value => value is >= 1 and <= 10000);
}
