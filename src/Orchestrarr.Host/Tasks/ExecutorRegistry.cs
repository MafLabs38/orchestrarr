using System.Collections.Concurrent;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Host.Tasks;

/// <summary>What the control plane knows of the executors: the last time each asked for work, and what it said of itself. In memory: it is only for the screen.</summary>
public sealed class ExecutorRegistry(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, (ExecutorHello Hello, DateTimeOffset At)> _seen = new(StringComparer.Ordinal);

    /// <summary>An executor that has not asked for work for this long is shown as gone.</summary>
    public static TimeSpan ConnectedFor { get; } = TimeSpan.FromSeconds(30);

    public void Seen(string name, ExecutorHello hello) => _seen[name] = (hello, time.GetUtcNow());

    public IReadOnlyList<ExecutorStatus> Snapshot()
    {
        var now = time.GetUtcNow();
        return [.. _seen.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new ExecutorStatus(
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

public sealed record ExecutorStatus(string Name, string Version, DateTimeOffset LastSeen, bool Connected, IReadOnlyList<TaskKind> Kinds, IReadOnlyList<string> Repositories, IReadOnlyList<DelegationInfo> Delegations, bool SecondFactorRequired);
