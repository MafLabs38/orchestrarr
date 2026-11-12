using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Storage;

namespace Orchestrarr.Host.Decisions;

/// <summary>
/// Writes down what became of the approved lines of the history once it is known, on a timer: the approval and the run are trimmed after a while, the history is not.
/// A read of the history writes nothing (ADR 0027).
/// </summary>
public sealed class HistoryConcluder(IProblemHistory history, IApprovalStore approvals, IBatchQueue queue, TimeProvider time, ILogger<HistoryConcluder> logger) : BackgroundService
{
    private const int LinesPerPass = 200;
    private const int MaxPagesPerPass = 25;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    ConcludeOnce();
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
                var open = history.Unconcluded(LinesPerPass, after);
                foreach (var entry in open)
                {
                    var outcome = HistoryOutcomeReader.Resolve(entry, byApproval, queue);
                    if (outcome != HistoryOutcome.InProgress)
                    {
                        history.Conclude(entry.Id, outcome);
                        concluded++;
                    }
                    after = entry.Id;
                }
                if (open.Count < LinesPerPass)
                {
                    break;
                }
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
