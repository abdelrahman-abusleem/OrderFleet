using System.Diagnostics;

namespace OrderFleet.Api.Diagnostics;



public sealed class SafeExceptionListener : IDisposable
{
    private readonly ActivityListener listener = new()
    {
        ShouldListenTo = source => source.Name is "OrderFleet" or "Npgsql" or "Microsoft.AspNetCore",
        ExceptionRecorder = (Activity _, Exception exception, ref TagList tags) => tags = new()
        {
            { "exception.message", "[redacted]" }, { "exception.stacktrace", "[redacted]" },
            { "exception.type", exception.GetType().FullName }
        }
    };
    public SafeExceptionListener() => ActivitySource.AddActivityListener(listener);
    public void Dispose() => listener.Dispose();
}
