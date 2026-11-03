using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Storage;

/// <summary>
/// The global stop, kept in the SQLite file so that a restart does not release it (ADR 0013). **Unknown means engaged**: a state that cannot be
/// read is read as engaged. Engaging always takes effect, even when it cannot be saved, because the stop must work when nothing else does; it
/// is then held in memory and <see cref="Saved"/> says it would not survive a restart. Releasing needs the release to be saved, and otherwise
/// the stop stays engaged. A fresh database starts released, unless it was created to start engaged (see <see cref="SqliteDatabase.Open"/>).
/// </summary>
public sealed class SqliteKillSwitch(SqliteDatabase database, int auditCapacity, ILogger<SqliteKillSwitch>? logger = null) : IKillSwitchControl
{
    private readonly ILogger _logger = logger ?? NullLogger<SqliteKillSwitch>.Instance;
    private readonly object _gate = new();

    // An engagement that could not be saved: it holds until a release is saved
    private volatile bool _held;

    // Whether the last read failed, so that a broken file is logged once and not on every poll of the status
    private volatile bool _unreadable;

    public bool Engaged => _held || ReadEngaged();

    public bool Saved => !_held;

    public ValueTask<bool> IsEngagedAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Engaged);

    /// <summary>Changes the stop without tracing it: for what is not a decision of a person (tests).</summary>
    public void Set(bool engaged)
    {
        // One change at a time: an engage and a release at once cannot leave the memory and the file disagreeing
        lock (_gate)
        {
            if (engaged)
            {
                _held = !TrySave(engaged: true);
                return;
            }

            if (!TrySave(engaged: false))
            {
                throw new KillSwitchUnavailableException();
            }
            _held = false;
        }
    }

    public ValueTask<KillSwitchChange> ChangeAsync(bool engaged, AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            try
            {
                // The stop and its trace are one write: a failure leaves nothing to undo, and a crash cannot leave a release without its trace
                var change = database.InWriteTransaction(connection =>
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
                });
                if (change != KillSwitchChange.NotTraced)
                {
                    _held = false;
                }
                else
                {
                    _logger.LogError("The global stop cannot be released: its release cannot be written to the audit trail, so it stays engaged");
                }
                if (change == KillSwitchChange.AppliedUntraced)
                {
                    _logger.LogError("The global stop is engaged, but the audit trail is full and cannot trace it");
                }
                return ValueTask.FromResult(change);
            }
            catch (Exception failure) when (failure is SqliteException or IOException or InvalidOperationException)
            {
                _logger.LogError(failure, "The state of the global stop cannot be saved");
                if (!engaged)
                {
                    return ValueTask.FromResult(KillSwitchChange.NotSaved);
                }
                _held = true;
                return ValueTask.FromResult(KillSwitchChange.AppliedUntraced);
            }
        }
    }

    private bool ReadEngaged()
    {
        try
        {
            using var connection = database.Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT engaged FROM kill_switch WHERE id = 1;";
            var engaged = command.ExecuteScalar() is long value && value != 0;
            _unreadable = false;
            return engaged;
        }
        catch (Exception failure) when (failure is SqliteException or IOException or InvalidOperationException)
        {
            if (!_unreadable)
            {
                _unreadable = true;
                _logger.LogError(failure, "The state of the global stop cannot be read, so it counts as engaged");
            }
            return true;
        }
    }

    private bool TrySave(bool engaged)
    {
        try
        {
            using var connection = database.Connect();
            Save(connection, engaged);
            return true;
        }
        catch (Exception failure) when (failure is SqliteException or IOException or InvalidOperationException)
        {
            _logger.LogError(failure, "The state of the global stop cannot be saved");
            return false;
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
