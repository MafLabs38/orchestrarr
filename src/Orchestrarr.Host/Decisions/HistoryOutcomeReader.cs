using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Host.Decisions;

/// <summary>What became of an approved line of the history, read from the approval and from the run where they still live.</summary>
internal static class HistoryOutcomeReader
{
    // From the approval when it is still there, otherwise from the run; when neither is left it is over (it cannot be known how), never "in progress" for good
    public static HistoryOutcome Resolve(HistoryEntry entry, IReadOnlyDictionary<Guid, Approval> approvals, IBatchQueue queue)
    {
        var approval = entry.ApprovalId is { } id && approvals.TryGetValue(id, out var found) ? found : null;
        var run = entry.BatchId is { } batch ? queue.Find(batch) : null;
        ApprovalState? state = approval?.State ?? run?.Summary.State switch
        {
            BatchState.Waiting => ApprovalState.Waiting,
            BatchState.Delivered => ApprovalState.Taken,
            BatchState.Refused => ApprovalState.Refused,
            BatchState.Expired => ApprovalState.Expired,
            BatchState.Done => ApprovalState.DryRunDone,
            _ => null,
        };
        if (state is null)
        {
            return HistoryOutcome.Ended;
        }
        IReadOnlyCollection<TaskResult?> results = state == ApprovalState.DryRunDone && run is not null
            ? [.. run.Tasks.Where(task => task.Spec.ApprovalId == entry.ApprovalId).Select(task => task.Result)]
            : [];
        return HistoryOutcomes.Of(state, results);
    }
}
