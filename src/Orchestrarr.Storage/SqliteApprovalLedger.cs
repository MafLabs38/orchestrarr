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
public sealed class SqliteApprovalLedger(SqliteDatabase database, int queueCapacity = StoreLimits.FileQueue, int auditCapacity = StoreLimits.FileAudit, ILogger<SqliteApprovalLedger>? logger = null) : IApprovalLedger
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
        if (approvals.Count > _queueCapacity - SqliteApprovalStore.Count(connection))
        {
            return (false, LedgerResult.StoreFull);
        }
        if (!AuditCapacityPolicy.Fits(_auditCapacity, SqliteAuditLog.Count(connection), SqliteAuditLog.RefusalCount(connection), entries))
        {
            return (false, LedgerResult.Unavailable);
        }
        try
        {
            foreach (var entry in entries)
            {
                SqliteAuditLog.Insert(connection, entry);
            }
            foreach (var approval in approvals)
            {
                SqliteApprovalStore.Insert(connection, approval);
            }
            return (true, LedgerResult.Recorded);
        }
        catch (SqliteException failure) when (SqliteDatabase.IsDuplicate(failure))
        {
            // Another process recorded the same decision first: nothing of this one stays, trace included
            return (false, LedgerResult.AlreadyThere);
        }
    }
}
