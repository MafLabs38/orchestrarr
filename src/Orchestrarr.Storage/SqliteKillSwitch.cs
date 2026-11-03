using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrarr.Core.Approvals;

namespace Orchestrarr.Storage;

/// <summary>
/// The global stop, kept in the SQLite file so that a restart does not release it (ADR 0010). **Unknown means engaged**: a state that cannot be
/// read is read as engaged. Engaging always takes effect, even when it cannot be saved, because the stop must work when nothing else does;
/// releasing needs the release to be saved, and otherwise the stop stays engaged. A fresh database starts released: nothing was ever stopped.
/// </summary>
public sealed class SqliteKillSwitch(SqliteDatabase database, ILogger<SqliteKillSwitch>? logger = null) : IKillSwitchControl
{
    private readonly ILogger _logger = logger ?? NullLogger<SqliteKillSwitch>.Instance;

    // An engagement that could not be saved: it holds until a release is saved
    private volatile bool _held;

    public bool Engaged => _held || ReadEngaged();

    public ValueTask<bool> IsEngagedAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Engaged);

    public void Set(bool engaged)
    {
        if (engaged)
        {
            _held = true;
            if (TrySave(engaged: true))
            {
                _held = false;
            }
            return;
        }

        if (!TrySave(engaged: false))
        {
            throw new KillSwitchUnavailableException();
        }
        _held = false;
    }

    private bool ReadEngaged()
    {
        try
        {
            using var connection = database.Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT engaged FROM kill_switch WHERE id = 1;";
            return command.ExecuteScalar() is long value && value != 0;
        }
        catch (Exception failure) when (failure is SqliteException or IOException or InvalidOperationException)
        {
            _logger.LogError(failure, "The state of the global stop cannot be read, so it counts as engaged");
            return true;
        }
    }

    private bool TrySave(bool engaged)
    {
        try
        {
            using var connection = database.Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO kill_switch (id, engaged, changed_at) VALUES (1, $engaged, $at) ON CONFLICT (id) DO UPDATE SET engaged = $engaged, changed_at = $at;";
            command.Parameters.AddWithValue("$engaged", engaged ? 1 : 0);
            command.Parameters.AddWithValue("$at", SqliteDatabase.Format(DateTimeOffset.UtcNow));
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
