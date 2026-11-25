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

    /// <summary>A runner that is already known and makes any call is there: its description stays what it said, only the time of its last sign changes. A runner never seen says nothing here (it has to say what it accepts first).</summary>
    public void Touch(string name)
    {
        var now = time.GetUtcNow();
        bool changed;
        lock (_gate)
        {
            if (!_seen.TryGetValue(name, out var before))
            {
                return;
            }
            changed = now - before.At > ConnectedFor;
            _seen[name] = (before.Hello, now);
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
        && before.FreeAgent == now.FreeAgent
        && SameLanes(before.Lanes, now.Lanes)
        && before.Kinds.SequenceEqual(now.Kinds)
        && before.Repositories.SequenceEqual(now.Repositories, StringComparer.Ordinal)
        && before.Delegations.SequenceEqual(now.Delegations);

    private static bool SameLanes(IReadOnlyDictionary<string, int>? before, IReadOnlyDictionary<string, int>? now)
    {
        var (was, after) = (before ?? new Dictionary<string, int>(), now ?? new Dictionary<string, int>());
        return was.Count == after.Count && was.All(pair => after.TryGetValue(pair.Key, out var other) && other == pair.Value);
    }

    /// <summary>How many missions the connected runners work on at once in a world: what the runners said (0 when none did, which is not a limit: it is shown, never enforced).</summary>
    private const int MaxLanes = 100;

    public int LanesOf(string world)
    {
        var now = time.GetUtcNow();
        return _seen.Values.Where(seen => now - seen.At <= ConnectedFor).Sum(seen => seen.Hello.Lanes?.Where(pair => string.Equals(pair.Key, world, StringComparison.OrdinalIgnoreCase)).Select(pair => Math.Clamp(pair.Value, 0, MaxLanes)).FirstOrDefault() ?? 0);
    }

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
            pair.Value.Hello.SecondFactorRequired,
            pair.Value.Hello.FreeAgent))];
    }
}

public sealed record RunnerStatus(string Name, string Version, DateTimeOffset LastSeen, bool Connected, IReadOnlyList<TaskKind> Kinds, IReadOnlyList<string> Repositories, IReadOnlyList<DelegationInfo> Delegations, bool SecondFactorRequired, bool FreeAgent);
