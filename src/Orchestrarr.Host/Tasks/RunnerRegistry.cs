using System.Collections.Concurrent;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Host.Tasks;

/// <summary>What the control plane knows of the runners: the last time each asked for work, and what it said of itself. In memory: it is only for the screen.</summary>
public sealed class RunnerRegistry(TimeProvider time)
{
    private readonly Lock _gate = new();

    private readonly ConcurrentDictionary<string, (RunnerHello Hello, DateTimeOffset At)> _seen = new(StringComparer.Ordinal);

    /// <summary>A runner that has not asked for work for this long is shown as gone.</summary>
    public static TimeSpan ConnectedFor { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Raised when a runner appears or comes back after having been silent, and when its description changes: not at every call, a runner asks every few seconds.</summary>
    public event Action? Changed;

    public void Seen(string name, RunnerHello hello)
    {
        var now = time.GetUtcNow();
        bool changed;
        // One compare and set: two claims at once from the same runner announce one change, not two
        lock (_gate)
        {
            changed = !_seen.TryGetValue(name, out var before) || now - before.At > ConnectedFor || !Same(before.Hello, hello);
            _seen[name] = (hello, now);
        }
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    // What a person sees of a runner: its version, what it accepts and whether it asks for the code. A list is compared by its values.
    private static bool Same(RunnerHello before, RunnerHello now) =>
        before.Version == now.Version
        && before.SecondFactorRequired == now.SecondFactorRequired
        && before.Kinds.SequenceEqual(now.Kinds)
        && before.Repositories.SequenceEqual(now.Repositories, StringComparer.Ordinal)
        && before.Delegations.SequenceEqual(now.Delegations);

    public IReadOnlyList<RunnerStatus> Snapshot()
    {
        var now = time.GetUtcNow();
        return [.. _seen.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new RunnerStatus(
            pair.Key,
            pair.Value.Hello.Version,
            pair.Value.At,
            now - pair.Value.At <= ConnectedFor,
            pair.Value.Hello.Kinds,
            pair.Value.Hello.Repositories,
            pair.Value.Hello.Delegations,
            pair.Value.Hello.SecondFactorRequired))];
    }
}

public sealed record RunnerStatus(string Name, string Version, DateTimeOffset LastSeen, bool Connected, IReadOnlyList<TaskKind> Kinds, IReadOnlyList<string> Repositories, IReadOnlyList<DelegationInfo> Delegations, bool SecondFactorRequired);
