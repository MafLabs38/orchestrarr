using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Approvals;

/// <summary>
/// The ledger of the sandbox: one step at a time, it checks what can be checked (the stop, the decisions already there, the room) before it
/// writes anything, then the trace, then the queue. It is the only writer of the queue, so what was checked cannot change before the write.
/// </summary>
public sealed class InMemoryApprovalLedger(InMemoryApprovalStore store, IAuditSink audit, IKillSwitchControl killSwitch) : IApprovalLedger, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<LedgerResult> TryRecordAsync(IReadOnlyList<Approval> approvals, IReadOnlyList<AuditEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(entries);
        await _gate.WaitAsync(cancellationToken);
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
            return store.TryAddRange(approvals) ? LedgerResult.Recorded : LedgerResult.Unavailable;
        }
        catch (StorageUnavailableException)
        {
            return LedgerResult.Unavailable;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
