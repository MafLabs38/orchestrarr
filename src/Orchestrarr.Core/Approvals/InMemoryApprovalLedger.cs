using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Core.Approvals;

/// <summary>
/// The ledger of the sandbox: one step at a time, it checks what can be checked (the stop, the decisions already there, the room) before it
/// writes anything, then the trace, then the queue. It is the only writer of the queue, so what was checked cannot change before the write.
/// </summary>
public sealed class InMemoryApprovalLedger(InMemoryApprovalStore store, IAuditSink audit, InMemoryKillSwitch killSwitch, InMemoryBatchQueue? queue = null) : IApprovalLedger
{
    public async ValueTask<LedgerResult> TryRecordAsync(IReadOnlyList<Approval> approvals, TaskBatch? batch, string? secondFactorCode, IReadOnlyList<AuditEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(entries);
        // The gate of the stop: an engagement is traced while it holds it, so the stop is read and the approval written between two engagements
        await killSwitch.Gate.WaitAsync(cancellationToken);
        try
        {
            // The stop is read again at the moment of writing: an approval is never recorded after the stop
            if (killSwitch.Engaged)
            {
                return LedgerResult.Stopped;
            }
            if (approvals.Any(store.Contains))
            {
                return LedgerResult.AlreadyThere;
            }
            if (store.Free < approvals.Count)
            {
                return LedgerResult.StoreFull;
            }
            // The trace first, all of it or none: no trace, no approval
            if (!await audit.TryRecordRangeAsync(entries, cancellationToken))
            {
                return LedgerResult.Unavailable;
            }
            // Checked above, under the gate, by the only writer: this cannot fail, and a trace is never left for an approval that was not kept
            if (!store.TryAddRange(approvals))
            {
                return LedgerResult.Unavailable;
            }
            if (batch is not null)
            {
                // The batch enters the queue in the same step as the approvals it carries out
                queue?.Add(batch, secondFactorCode);
            }
            return LedgerResult.Recorded;
        }
        catch (StorageUnavailableException)
        {
            return LedgerResult.Unavailable;
        }
        finally
        {
            killSwitch.Gate.Release();
        }
    }
}
