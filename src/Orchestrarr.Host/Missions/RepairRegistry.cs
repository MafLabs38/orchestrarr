using System.Collections.Concurrent;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Missions;

/// <summary>A repair that a staging runner made and handed over, waiting for the gateway to put it in a pull request.</summary>
internal sealed record PendingRepair(string OriginalHead, IReadOnlyList<PatchedFile> Files);

/// <summary>The pull request that carries a repair: once it exists, it is the one that the Git and release worlds are about, and the pull request it replaces is only where the problem started.</summary>
internal sealed record FixPullRequest(int Number, string Head, string OriginalHead);

/// <summary>
/// What the control plane keeps of the repairs (ADR 0044), in memory: the files of a repair (checked against the policy before they are kept) and the pull request that the gateway opened for them. In memory because the test bed is, and
/// because a repair that is lost is made again by the loop of the problem, not trusted from a store that nobody can check. A problem has one repair at a time.
/// </summary>
internal sealed class RepairRegistry
{
    private readonly ConcurrentDictionary<string, PendingRepair> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FixPullRequest> _fixes = new(StringComparer.Ordinal);

    /// <summary>Keeps the files of a repair. A new repair of the same problem replaces the old one and forgets the pull request of the old one: it is a new run.</summary>
    public void Keep(string problemId, string originalHead, IReadOnlyList<PatchedFile> files)
    {
        _pending[problemId] = new PendingRepair(originalHead, files);
        _fixes.TryRemove(problemId, out _);
    }

    public PendingRepair? PendingOf(string problemId) => _pending.GetValueOrDefault(problemId);

    public FixPullRequest? FixOf(string problemId) => _fixes.GetValueOrDefault(problemId);

    public void Opened(string problemId, int number, string head, string originalHead) => _fixes[problemId] = new FixPullRequest(number, head, originalHead);

    /// <summary>The pull request that the problem is about now: the one that carries its repair, else the one it started from.</summary>
    public (int Number, string Head)? Effective(string problemId, PlanEntry entry)
    {
        if (FixOf(problemId) is { } fix)
        {
            return (fix.Number, fix.Head);
        }
        return entry is { PullRequest: { } number, HeadSha: { } head } ? (number, head) : null;
    }
}
