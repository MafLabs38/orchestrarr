using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrarr.Core.Approvals;

namespace Orchestrarr.Storage;

/// <summary>
/// The global stop, kept in the SQLite file so that a restart does not release it (ADR 0013). **Unknown means engaged**: a state that cannot be
/// read is read as engaged. Engaging always takes effect, even when it cannot be saved, because the stop must work when nothing else does; it
/// is then held in memory and <see cref="Saved"/> says it would not survive a restart. Releasing needs the release to be saved, and otherwise
/// the stop stays engaged. A fresh database starts released, unless it was created to start engaged (see <see cref="SqliteDatabase.Open"/>).
/// </summary>
public sealed class SqliteKillSwitch(SqliteDatabase database, ILogger<SqliteKillSwitch>? logger = null) : IKillSwitchControl
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
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO kill_switch (id, engaged) VALUES (1, $engaged) ON CONFLICT (id) DO UPDATE SET engaged = $engaged;";
            command.Parameters.AddWithValue("$engaged", engaged ? 1 : 0);
            command.ExecuteNonQuery();
            return true;
        }
        catch (Exception failure) when (failure is SqliteException or IOException or InvalidOperationException)
        {
            _logger.LogError(failure, "The state of the global stop cannot be saved");
            return false;
        }
    }
}
