using System.Collections.Concurrent;
using OrderFleet.Application.Identity;

namespace OrderFleet.Api.Realtime;



public sealed class OrderConnections
{
    private readonly ConcurrentDictionary<string, OrderConnection> connections = new();
    private readonly object admission = new();

    internal bool Add(string id, Actor actor, DateTimeOffset expires)
    {
        lock (admission)
        {
            if (connections.Count >= 1000 || connections.Values.Count(x => x.Actor.Id == actor.Id) >= 5) return false;
            return connections.TryAdd(id, new(id, actor, expires));
        }
    }
    internal OrderConnection? Find(string id) => connections.GetValueOrDefault(id);
    internal OrderConnection[] Snapshot() => connections.Values.ToArray();
    internal void Remove(string id)
    {
        if (connections.TryRemove(id, out var entry)) entry.Active = false;
    }
}

internal sealed class OrderConnection(string id, Actor actor, DateTimeOffset expires)
{
    internal string Id { get; } = id;
    internal Actor Actor { get; } = actor;
    internal DateTimeOffset ExpiresAt { get; } = expires;
    internal volatile bool Active = true;
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal Dictionary<Guid, OrderSubscription> Subscriptions { get; } = new();
}

internal sealed class OrderSubscription(string group, long sequence)
{
    internal string Group { get; } = group;
    internal long LastSequence { get; set; } = sequence;
    internal DateTimeOffset? LastLocationObservedAt { get; set; }
    internal DateTimeOffset? LastLocationSentAt { get; set; }
}
