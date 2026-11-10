using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Core.Approvals;

public enum LedgerResult
{
    Recorded,

    /// <summary>One of the decisions is already there: nothing was written.</summary>
    AlreadyThere,

    /// <summary>The queue or the trail has no room for all of it: nothing was written.</summary>
    StoreFull,

    /// <summary>The global stop is engaged, as the ledger reads it in the write itself: nothing was written.</summary>
    Stopped,

    /// <summary>The queue or the trail cannot be written: nothing was written.</summary>
    Unavailable,
}

/// <summary>
/// Records the approvals and their trace **together**, all or nothing. What is traced is what is stored: when both live in one file, one
/// transaction writes both, so that a failure of the second can never leave a trace for an approval that was not kept (ADR 0010, 0013).
/// </summary>
public interface IApprovalLedger
{
    // The implementations in a file are synchronous: a call can wait for the file, no longer than its busy timeout.

    /// <summary>
    /// Records the approvals, the batch of tasks that carries them out (if any) and the trace, all or nothing. The batch is the only way a
    /// task enters the queue (ADR 0017): this method is the one entry.
    /// </summary>
    ValueTask<LedgerResult> TryRecordAsync(IReadOnlyList<Approval> approvals, TaskBatch? batch, string? secondFactorCode, IReadOnlyList<AuditEntry> entries, CancellationToken cancellationToken);
}

public static class ApprovalLedgerExtensions
{
    /// <summary>Approvals and their trace without tasks: what the tests of the stores use.</summary>
    public static ValueTask<LedgerResult> TryRecordAsync(this IApprovalLedger ledger, IReadOnlyList<Approval> approvals, IReadOnlyList<AuditEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return ledger.TryRecordAsync(approvals, null, null, entries, cancellationToken);
    }
}
