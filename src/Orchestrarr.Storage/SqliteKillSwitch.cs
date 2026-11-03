using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Storage;

/// <summary>
/// The global stop, kept in the SQLite file so that a restart does not release it (ADR 0013). **Unknown means engaged**: a state that cannot be
/// read is read as engaged. Engaging always takes effect, even when it cannot be saved, because the stop must work when nothing else does; it
/// is then held in memory and <see cref="State"/> says it would not survive a restart. Releasing needs the release to be saved, and otherwise
/// the stop stays engaged. A fresh database starts released, unless it was created to start engaged (see <see cref="SqliteDatabase.Open"/>).
/// </summary>
public sealed class SqliteKillSwitch(SqliteDatabase database, int auditCapacity, ILogger<SqliteKillSwitch>? logger = null) : IKillSwitchControl
{
    private readonly ILogger _logger = logger ?? NullLogger<SqliteKillSwitch>.Instance;
    private readonly object _gate = new();

    // An engagement that could not be saved: it holds until a release is saved
    private volatile bool _held;

    // Engagements waiting for the file, or for a release that has it: they count from the moment they are asked for, and a release that finishes first cannot undo them
    private int _engaging;

    // Whether the last read failed, so that a broken file is logged once and not on every poll of the status
    private volatile bool _unreadable;

    public bool Engaged => State().Engaged;

    // One read of the file for both answers. An unreadable file is engaged, and not saved: the state is unknown.
    public KillSwitchState State()
    {
        if (Volatile.Read(ref _engaging) > 0 || _held)
        {
            return new KillSwitchState(Engaged: true, Saved: false);
        }
        var engaged = ReadEngaged();
        return new KillSwitchState(engaged, Saved: !_unreadable);
    }

    public ValueTask<bool> IsEngagedAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Engaged);

    public ValueTask<KillSwitchChange> ChangeAsync(bool engaged, AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (engaged)
        {
            // Engaging always takes effect, and a client that gave up is no reason to skip it: it counts from now, before it gets the file
            Interlocked.Increment(ref _engaging);
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        try
        {
            lock (_gate)
            {
                return ValueTask.FromResult(Write(engaged, entry));
            }
        }
        finally
        {
            if (engaged)
            {
                Interlocked.Decrement(ref _engaging);
            }
        }
    }

    // The invariant of the three flags: Engaged is true while an engagement is in flight (_engaging), while one is held because it could not be
    // saved (_held), or when the file says so (or cannot be read). _held is set only by an engagement whose write failed, and cleared only by a write
    // that succeeded: a release that did not happen (NotTraced, NotSaved) leaves it as it was.
    private KillSwitchChange Write(bool engaged, AuditEntry entry)
    {
        try
        {
            // The stop and its trace are one write: a failure leaves nothing to undo, and a crash cannot leave a release without its trace
            var change = database.InWriteTransaction(connection => Apply(connection, engaged, entry));
            if (change != KillSwitchChange.NotTraced)
            {
                _held = false;
            }
            LogOutcome(change);
            return change;
        }
        // Engaging takes effect whatever went wrong, except what nothing can recover from
        catch (Exception failure) when (SqliteDatabase.IsUnavailable(failure) || (engaged && IsRecoverableForEngage(failure)))
        {
            return OnWriteFailed(failure, engaged);
        }
    }

    private (bool Commit, KillSwitchChange Result) Apply(SqliteConnection connection, bool engaged, AuditEntry entry)
    {
        var traced = AuditCapacityPolicy.Fits(auditCapacity, SqliteAuditLog.Count(connection), SqliteAuditLog.RefusalCount(connection), [entry]);
        if (!engaged && !traced)
        {
            return (false, KillSwitchChange.NotTraced);
        }
        Save(connection, engaged);
        if (traced)
        {
            SqliteAuditLog.Insert(connection, entry);
        }
        return (true, traced ? KillSwitchChange.Applied : KillSwitchChange.AppliedUntraced);
    }

    // What an engagement survives: everything, so that the stop works when nothing else does, except running out of memory, from which nothing is
    // known to be in a state to be trusted. (A stack overflow or an abort cannot be caught at all.)
    private static bool IsRecoverableForEngage(Exception failure) => failure is not OutOfMemoryException;

    private KillSwitchChange OnWriteFailed(Exception failure, bool engaged)
    {
        // A failure that is not the storage's is a bug, and is logged as one, so that it does not pass for a full disk
        if (SqliteDatabase.IsUnavailable(failure))
        {
            _logger.LogError(failure, "The state of the global stop cannot be saved");
        }
        else
        {
            _logger.LogCritical(failure, "A failure that is not the storage's stopped the global stop from being saved: it is held in memory, and this is a bug");
        }
        if (!engaged)
        {
            return KillSwitchChange.NotSaved;
        }
        _held = true;
        return KillSwitchChange.AppliedUntraced;
    }

    private void LogOutcome(KillSwitchChange change)
    {
        if (change == KillSwitchChange.NotTraced)
        {
            _logger.LogError("The global stop cannot be released: its release cannot be written to the audit trail, so it stays engaged");
        }
        else if (change == KillSwitchChange.AppliedUntraced)
        {
            _logger.LogError("The global stop is engaged, but the audit trail is full and cannot trace it");
        }
    }

    private bool ReadEngaged()
    {
        try
        {
            using var connection = database.Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT engaged FROM kill_switch WHERE id = 1;";
            // A row that is missing is unknown, and unknown is engaged
            var engaged = command.ExecuteScalar() is not long value || value != 0;
            _unreadable = false;
            return engaged;
        }
        catch (Exception failure) when (SqliteDatabase.IsUnavailable(failure))
        {
            if (!_unreadable)
            {
                _unreadable = true;
                _logger.LogError(failure, "The state of the global stop cannot be read, so it counts as engaged");
            }
            return true;
        }
    }

    private static void Save(SqliteConnection connection, bool engaged)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO kill_switch (id, engaged) VALUES (1, $engaged) ON CONFLICT (id) DO UPDATE SET engaged = $engaged;";
        command.Parameters.AddWithValue("$engaged", engaged ? 1 : 0);
        command.ExecuteNonQuery();
    }
}
