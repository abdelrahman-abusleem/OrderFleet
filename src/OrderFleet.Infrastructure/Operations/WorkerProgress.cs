using Microsoft.Extensions.Options;
using OrderFleet.Application.Operations;

namespace OrderFleet.Infrastructure.Operations;

public sealed class WorkerHealthOptions
{
    public int StartupGraceSeconds { get; set; } = 60;
    public int StallSeconds { get; set; } = 90;
    public int FailureGraceSeconds { get; set; } = 30;
    public int ProbeSeconds { get; set; } = 5;
}

// Explicit process-local operational state only. Correctness still lives in DB
// leases/transactions. Monotonic time avoids wall-clock adjustments hiding stalls.
public sealed class WorkerProgress(TimeProvider time, IOptions<WorkerHealthOptions> options)
{
    public static readonly IReadOnlyList<string> Names = Array.AsReadOnly(new[] { "outbox", "payments", "dispatch", "realtime", "financial", "recovery", "operations" });
    private sealed class Entry(long now)
    {
        public string State = "Starting";
        public long Changed = now;
        public long Progress = now;
        public bool Busy;
        public DateTimeOffset? LastSuccess;
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = Names.ToDictionary(x => x, _ => new Entry(time.GetTimestamp()));
    public void Begin(string worker) { lock (gate) entries[worker] = new(time.GetTimestamp()); }
    public void BeginWork(string worker)
    {
        lock (gate) { var entry = entries[worker]; entry.Busy = true; entry.Progress = time.GetTimestamp(); }
    }
    public void Success(string worker, bool idleProbe = false)
    {
        lock (gate)
        {
            var entry = entries[worker];
            if (idleProbe && entry.Busy) return; // A live socket must not conceal a stuck handler.
            entry.Busy = false; entry.State = "Running"; entry.Progress = time.GetTimestamp(); entry.LastSuccess = time.GetUtcNow();
        }
    }
    public void Failure(string worker)
    {
        lock (gate)
        {
            var entry = entries[worker];
            if (entry.State != "Failed") entry.Changed = time.GetTimestamp();
            entry.State = "Failed"; entry.Busy = false;
        }
    }
    public void Stop(string worker) { lock (gate) { entries[worker].State = "Stopped"; entries[worker].Busy = false; } }
    public void Disable(string worker) { lock (gate) { entries[worker].State = "Disabled"; entries[worker].Busy = false; } }
    public IReadOnlyList<WorkerDiagnostic> Snapshot()
    {
        lock (gate)
        {
            var now = time.GetTimestamp(); var policy = options.Value;
            return entries.Select(pair =>
            {
                var entry = pair.Value;
                var age = Math.Max(0, time.GetElapsedTime(entry.Progress, now).TotalSeconds);
                var stateAge = Math.Max(0, time.GetElapsedTime(entry.Changed, now).TotalSeconds);
                var state = entry.State == "Running" && age > policy.StallSeconds ? "Stalled" : entry.State;
                var ready = state == "Running" || state == "Starting" && stateAge <= policy.StartupGraceSeconds
                    || state == "Failed" && stateAge <= policy.FailureGraceSeconds && age <= policy.StallSeconds;
                return new WorkerDiagnostic(pair.Key, state, ready, age, entry.LastSuccess);
            }).ToArray();
        }
    }
}
