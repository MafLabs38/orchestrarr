using Orchestrarr.Core.Launch;

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
    ValueTask<LedgerResult> TryRecordAsync(IReadOnlyList<Approval> approvals, IReadOnlyList<AuditEntry> entries, CancellationToken cancellationToken);
}
