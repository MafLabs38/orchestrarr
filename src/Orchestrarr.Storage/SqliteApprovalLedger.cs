using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Storage;

/// <summary>
/// Writes the approvals and their trace in **one transaction** of the file: either both are kept or neither is, so that the trail can never
/// say "approval recorded" for an approval that is not in the queue (ADR 0010 rule 4, ADR 0013). A full queue, a full trail, a decision already
/// there and a file that cannot be written each refuse without leaving anything behind.
/// </summary>
public sealed class SqliteApprovalLedger(SqliteDatabase database, IKillSwitchControl killSwitch, int queueCapacity = StoreLimits.FileQueue, int auditCapacity = StoreLimits.FileAudit, ILogger<SqliteApprovalLedger>? logger = null) : IApprovalLedger
{
    private readonly int _queueCapacity = queueCapacity > 0 ? queueCapacity : throw new ArgumentOutOfRangeException(nameof(queueCapacity), "The capacity must be greater than zero.");
    private readonly int _auditCapacity = int.IsNegative(auditCapacity) ? throw new ArgumentOutOfRangeException(nameof(auditCapacity), "The capacity cannot be negative.") : auditCapacity;
    private readonly ILogger _logger = logger ?? NullLogger<SqliteApprovalLedger>.Instance;

    public ValueTask<LedgerResult> TryRecordAsync(IReadOnlyList<Approval> approvals, IReadOnlyList<AuditEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(entries);
        cancellationToken.ThrowIfCancellationRequested();
        if (approvals.Select(SqliteApprovalStore.Key).Distinct().Count() != approvals.Count)
        {
            return ValueTask.FromResult(LedgerResult.AlreadyThere);
        }

        try
        {
            return ValueTask.FromResult(database.InWriteTransaction(connection => Write(connection, approvals, entries)));
        }
        catch (Exception failure) when (SqliteDatabase.IsUnavailable(failure))
        {
            _logger.LogError(failure, "The approvals and their trace cannot be written, so nothing is recorded");
            return ValueTask.FromResult(LedgerResult.Unavailable);
        }
    }

    private (bool Commit, LedgerResult Result) Write(SqliteConnection connection, IReadOnlyList<Approval> approvals, IReadOnlyList<AuditEntry> entries)
    {
        // The stop is read inside the write: an engagement either commits before this transaction, and is seen here, or waits for it
        // (or is counted as in flight), so that an approval is never recorded after the stop
        // A separate connection by design: in WAL mode a reader never blocks the writer that holds the lock here
        if (killSwitch.Engaged)
        {
            return (false, LedgerResult.Stopped);
        }
        if (!SqliteApprovalStore.HasRoom(connection, _queueCapacity, approvals.Count))
        {
            return (false, LedgerResult.StoreFull);
        }
        if (!AuditCapacityPolicy.Fits(_auditCapacity, SqliteAuditLog.Count(connection), SqliteAuditLog.RefusalCount(connection), entries))
        {
            return (false, LedgerResult.Unavailable);
        }
        foreach (var entry in entries)
        {
            SqliteAuditLog.Insert(connection, entry);
        }
        // Another process may have recorded the same decision first: nothing of this one stays, trace included
        return SqliteApprovalStore.InsertAll(connection, approvals) ? (true, LedgerResult.Recorded) : (false, LedgerResult.AlreadyThere);
    }
}
