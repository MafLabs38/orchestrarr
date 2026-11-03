using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>The ledger of the sandbox: the trace first, then the queue, whose room was counted before. Two stores in memory cannot fail apart.</summary>
public sealed class InMemoryApprovalLedger(InMemoryApprovalStore store, IAuditSink audit) : IApprovalLedger
{
    public async ValueTask<LedgerResult> TryRecordAsync(IReadOnlyList<Approval> approvals, IReadOnlyList<AuditEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(entries);
        try
        {
            if (store.Free < approvals.Count)
            {
                return LedgerResult.StoreFull;
            }
            // The trace first, all of it or none: no trace, no approval
            if (!await audit.TryRecordRangeAsync(entries, cancellationToken))
            {
                return LedgerResult.Unavailable;
            }
            return store.TryAddRange(approvals) ? LedgerResult.Recorded : LedgerResult.StoreFull;
        }
        catch (StorageUnavailableException)
        {
            return LedgerResult.Unavailable;
        }
    }
}
