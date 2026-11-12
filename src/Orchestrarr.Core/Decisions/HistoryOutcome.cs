using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Core.Decisions;

/// <summary>What became of a line of the history, in one word: read from the approval and from the run, where it already lives (ADR 0027).</summary>
public enum HistoryOutcome
{
    /// <summary>The problem was set aside by a person.</summary>
    Skipped,

    /// <summary>Approved, and the Runner has not finished.</summary>
    InProgress,

    /// <summary>The run ended and the checks pass.</summary>
    Passed,

    /// <summary>The run ended and a check failed, or the agent did not repair.</summary>
    Failed,

    /// <summary>An agent repaired it: the patch is in the trace of the run.</summary>
    Repaired,

    /// <summary>The Runner refused the batch (the code, the rights, the commit that moved).</summary>
    Refused,

    /// <summary>No Runner took it in time.</summary>
    Expired,

    /// <summary>The run ended and said nothing about the code (a dry run).</summary>
    Ended,
}

public static class HistoryOutcomes
{
    /// <summary>
    /// The outcome of an approved line. <paramref name="results"/> are the results of the tasks of this approval in its run, when the run is known. A repair beats a failure
    /// (the failure was what it repaired), a failure beats a pass (one failed step is a failed decision), and a pass beats nothing.
    /// </summary>
    public static HistoryOutcome Of(ApprovalState? state, IReadOnlyCollection<TaskResult?> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        switch (state)
        {
            case null:
            case ApprovalState.Waiting:
            case ApprovalState.Taken:
                return HistoryOutcome.InProgress;
            case ApprovalState.Refused:
                return HistoryOutcome.Refused;
            case ApprovalState.Expired:
                return HistoryOutcome.Expired;
        }
        if (results.Contains(TaskResult.Repaired))
        {
            return HistoryOutcome.Repaired;
        }
        if (results.Contains(TaskResult.Failed))
        {
            return HistoryOutcome.Failed;
        }
        if (results.Contains(TaskResult.Refused))
        {
            return HistoryOutcome.Refused;
        }
        return results.Contains(TaskResult.Passed) ? HistoryOutcome.Passed : HistoryOutcome.Ended;
    }
}
