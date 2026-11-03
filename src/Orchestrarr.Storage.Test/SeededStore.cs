using Orchestrarr.Core.Approvals;

namespace Orchestrarr.Storage.Test;

/// <summary>A store to read and the way to fill it: the product writes an approval through the ledger only, so the tests do too, with no trace to record.</summary>
internal sealed class SeededStore(SqliteDatabase database, int capacity = StoreLimits.FileQueue)
{
    private readonly SqliteApprovalStore _reader = new(database, capacity);
    private readonly SqliteApprovalLedger _ledger = new(database, new InMemoryKillSwitch(), capacity);

    public int Free => _reader.Free;

    public bool Contains(Approval approval) => _reader.Contains(approval);

    public IReadOnlyList<Approval> List() => _reader.List();

    public bool TryAddRange(IReadOnlyList<Approval> approvals) =>
        _ledger.TryRecordAsync(approvals, [], TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult() == LedgerResult.Recorded;
}
