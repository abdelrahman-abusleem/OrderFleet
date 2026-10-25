using System.Diagnostics;

namespace OrderFleet.Api.Diagnostics;

// ActivityEvent tags are immutable after capture. Redact through the .NET public
// exception-recorder callback before the runtime creates the exception event.
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
