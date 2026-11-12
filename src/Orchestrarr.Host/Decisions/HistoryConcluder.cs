using Orchestrarr.Core.Applications;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Core.WorkItems;
using Orchestrarr.Host.Live;
using Orchestrarr.Host.Tasks;
using Orchestrarr.Storage;

namespace Orchestrarr.Host.Decisions;

/// <summary>
/// Writes down what became of the approved lines of the history once it is known, on a timer: the approval and the run are trimmed after a while, the history is not.
/// It also ends the skips of the problems that are gone, so that one that comes back is seen again. A read of the history writes nothing (ADR 0027).
/// </summary>
public sealed class HistoryConcluder(IProblemHistory history, IApprovalStore approvals, IBatchQueue queue, WorkItemCatalog catalog, IApplicationCatalog applications, RunnerRegistry runners,
    RelevancePolicy policy, IAuditSink audit, ILiveNotifier live, TimeProvider time, ILogger<HistoryConcluder> logger) : BackgroundService
{
    private const int LinesPerPass = 200;
    private const int MaxPagesPerPass = 25;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    // The skips whose problem was not in the table the first time it was missed, with the reading that missed it: a problem is gone only if two readings agree
    private readonly Dictionary<ActiveSkip, DateTimeOffset> _missing = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var changed = ConcludeOnce() + await LapseSkipsOfGoneProblemsAsync(stoppingToken);
                    if (changed > 0)
                    {
                        live.Publish(LiveTopics.Problems);
                    }
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    // A pass that fails must not stop the host: the next one tries again
                    logger.LogError(failure, "The pass that writes down the outcomes of the history failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping
        }
    }

    /// <summary>
    /// Ends the skips whose problem is gone from the table. It looks at what was read last and reads nothing itself (a timer must not poll the sources), and only when the
    /// table is whole: a source that cannot be read, or pull requests not read, would make every problem look gone. A problem is gone when two different readings agree
    /// that it is missing. Each skip is written to the trail first; one that cannot be traced is left for the next pass.
    /// </summary>
    public async Task<int> LapseSkipsOfGoneProblemsAsync(CancellationToken cancellationToken)
    {
        var skips = history.ActiveSkips();
        if (skips.Count == 0)
        {
            return 0;
        }
        if (catalog.Peek() is not { } report || report.Stale || report.Error is not null || report.SourceKind == FleetSourceKinds.None || report.ReadAt is not { } readAt)
        {
            return 0;
        }
        var table = ProblemsEndpoints.FullTable(report, applications, runners, queue, time, policy);
        if (table.Any(row => row.Kind == ProblemKind.PullRequestsNotRead))
        {
            return 0;
        }
        var missing = MissingSkips(skips, table, report.Items);
        foreach (var key in _missing.Keys.Where(key => !missing.Contains(key)).ToList())
        {
            _missing.Remove(key);
        }
        var gone = new List<ActiveSkip>();
        foreach (var skip in missing)
        {
            if (!_missing.TryGetValue(skip, out var first))
            {
                _missing[skip] = readAt;
                continue;
            }
            if (first >= readAt)
            {
                continue;
            }
            if (await audit.TryRecordAsync(new AuditEntry(time.GetUtcNow(), DecisionAuditEvents.ProblemSkipLapsed, "system", null, skip.RowId.Length > 120 ? skip.RowId[..120] : skip.RowId), cancellationToken))
            {
                gone.Add(skip);
            }
        }
        return gone.Count == 0 ? 0 : history.LapseSkips(gone);
    }

    /// <summary>
    /// The skips whose problem is not the one in the table: gone, or still there with another text. A row about a pull request is missing for good only if every list of pull
    /// requests was read whole: a list cut at the bound of the reader, one that failed or one still being read does not say the pull request is gone.
    /// </summary>
    internal static IReadOnlyList<ActiveSkip> MissingSkips(IReadOnlyList<ActiveSkip> skips, IReadOnlyList<ProblemRow> table, IReadOnlyList<WorkItem> items)
    {
        var present = table.Select(row => (row.Id, row.Description)).ToHashSet();
        var pullRequestsWhole = items.All(item => item.PullRequests is { Status: PullRequestsStatus.Read or PullRequestsStatus.NotNeeded, Truncated: false });
        return [.. skips.Where(skip => !present.Contains((skip.RowId, skip.Target)) && (pullRequestsWhole || !skip.RowId.StartsWith(DecisionTable.PullRequestRowPrefix, StringComparison.Ordinal))).Distinct()];
    }

    /// <summary>One pass over the approved lines without an outcome; returns how many were concluded.</summary>
    public int ConcludeOnce()
    {
        try
        {
            var byApproval = approvals.List(ProblemsEndpoints.ApprovalsLookedAt).ToDictionary(approval => approval.Id);
            var concluded = 0;
            // Page by page from the oldest: lines still in progress do not hold back the ones behind them
            long after = 0;
            for (var pages = 0; pages < MaxPagesPerPass; pages++)
            {
                var scan = history.ReadUnconcluded(LinesPerPass, after);
                foreach (var entry in scan.Lines)
                {
                    var outcome = HistoryOutcomeReader.Resolve(entry, byApproval, queue);
                    if (outcome != HistoryOutcome.InProgress)
                    {
                        history.Conclude(entry.Id, outcome);
                        concluded++;
                    }
                }
                // Where the read stopped, a line that could not be read included
                if (scan.Cursor is not { } next)
                {
                    break;
                }
                after = next;
            }
            return concluded;
        }
        catch (StorageUnavailableException failure)
        {
            logger.LogWarning(failure, "The outcomes of the history could not be written down; the next pass will try again");
            return 0;
        }
    }
}
