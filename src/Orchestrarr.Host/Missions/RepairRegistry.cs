using System.Collections.Concurrent;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Missions;

/// <summary>A repair that a staging runner made and handed over, waiting for the gateway to put it in a pull request.</summary>
internal sealed record PendingRepair(string OriginalHead, IReadOnlyList<PatchedFile> Files, PendingBranch? Branch = null);

/// <summary>
/// The branch of a worker agent that says it has finished (ADR 0048), as the control plane read it on the forge: its name, the commit it was audited at, the paths it changes and what the controller reads of it. The pull request
/// is opened from this branch at this commit and from nothing else.
/// </summary>
internal sealed record PendingBranch(string Name, string Sha, IReadOnlyList<string> Paths, string Reading);

/// <summary>The pull request that carries a repair: once it exists, it is the one that the Git and release worlds are about, and the pull request it replaces is only where the problem started.</summary>
internal sealed record FixPullRequest(int Number, string Head, string OriginalHead);

/// <summary>
/// What the control plane keeps of the repairs (ADR 0044), in memory: the files of a repair (checked against the policy before they are kept) and the pull request that the gateway opened for them. In memory because the test bed is, and
/// because a repair that is lost is made again by the loop of the problem, not trusted from a store that nobody can check. A problem has one repair at a time.
/// </summary>
internal sealed class RepairRegistry : IProblemRestarts
{
    private readonly ConcurrentDictionary<string, PendingRepair> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FixPullRequest> _fixes = new(StringComparer.Ordinal);

    /// <summary>Keeps the files of a repair. A new repair of the same problem replaces the old one and forgets the pull request of the old one: it is a new run.</summary>
    public void Keep(string problemId, string originalHead, IReadOnlyList<PatchedFile> files)
    {
        _pending[problemId] = new PendingRepair(originalHead, files);
        _fixes.TryRemove(problemId, out _);
    }

    /// <summary>Keeps the branch of an agent that finished, read from the forge: a new one replaces the old one and forgets the pull request of the old one.</summary>
    public void KeepBranch(string problemId, string originalHead, PendingBranch branch)
    {
        _pending[problemId] = new PendingRepair(originalHead, [], branch);
        _fixes.TryRemove(problemId, out _);
    }

    /// <summary>Forgets what is held of a problem's repair: what a restart of the control plane does to all of them.</summary>
    public void Forget(string problemId)
    {
        _pending.TryRemove(problemId, out _);
        _fixes.TryRemove(problemId, out _);
    }

    /// <summary>A problem that starts again from a new base (the integration branch turned red after its merge): the repair and the pull request held are about a change that is merged.</summary>
    public void Restarted(string problemId) => Forget(problemId);

    public PendingRepair? PendingOf(string problemId) => _pending.GetValueOrDefault(problemId);

    /// <summary>The pull request that the gateway opened for the repair. **Only what the control plane itself did is believed**: a runner's dossier is not a source of it. After a restart nothing is held, and a repair that was made is then outstanding (the merge and the deployment are refused, the problem goes back).</summary>
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
