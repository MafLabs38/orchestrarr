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
internal sealed partial class RepairRegistry(IMissionStore missions, IDossierStore dossiers)
{
    private readonly ConcurrentDictionary<string, PendingRepair> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FixPullRequest> _fixes = new(StringComparer.Ordinal);

    /// <summary>Keeps the files of a repair. A new repair of the same problem replaces the old one and forgets the pull request of the old one: it is a new run.</summary>
    public void Keep(string problemId, string originalHead, IReadOnlyList<PatchedFile> files)
    {
        _pending[problemId] = new PendingRepair(originalHead, files);
        _fixes.TryRemove(problemId, out _);
    }

    /// <summary>Forgets what is held of a problem's repair: what a restart of the control plane does to all of them.</summary>
    public void Forget(string problemId)
    {
        _pending.TryRemove(problemId, out _);
        _fixes.TryRemove(problemId, out _);
    }

    public PendingRepair? PendingOf(string problemId) => _pending.GetValueOrDefault(problemId);

    /// <summary>
    /// The pull request that carries the repair: what the gateway opened, or, when the control plane has restarted since, what the Git world's dossier recorded of it (the dossiers are kept, this registry is not). A restart between the
    /// opening and that dossier is not recovered: the repair is then outstanding, and the problem goes back.
    /// </summary>
    public FixPullRequest? FixOf(string problemId)
    {
        if (_fixes.TryGetValue(problemId, out var held))
        {
            return held;
        }
        var recorded = missions.ForProblem(problemId).Where(mission => mission.World == World.Git).Select(mission => dossiers.Get(mission.Id)).LastOrDefault(dossier => dossier is not null)
            ?.EvidenceDigests.Select(digest => RecordedFix().Match(digest)).FirstOrDefault(match => match.Success);
        return recorded is null ? null : new FixPullRequest(int.Parse(recorded.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), recorded.Groups[2].Value, recorded.Groups[3].Value);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^repair:pr=(\d{1,9});head=([0-9a-f]{40});from=([0-9a-f]{40})$", System.Text.RegularExpressions.RegexOptions.None, 250)]
    private static partial System.Text.RegularExpressions.Regex RecordedFix();

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
