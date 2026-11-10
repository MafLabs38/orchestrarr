using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Settings;

namespace Orchestrarr.Storage;

/// <summary>The options in the SQLite file (ADR 0023): the value, when it was set and by whom. A read that fails raises the unavailable failure, which the service reads as the safe value.</summary>
public sealed class SqliteOptionStore(SqliteDatabase database) : IOptionStore
{
    public bool? Get(string id) => SqliteDatabase.Guard(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM options WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is long value ? value != 0 : (bool?)null;
    });

    public bool TrySet(string id, bool value, DateTimeOffset at, string actor)
    {
        try
        {
            return SqliteDatabase.Guard(() =>
            {
                using var connection = database.Connect();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO options (id, value, changed_at, changed_by) VALUES ($id, $value, $at, $actor) ON CONFLICT (id) DO UPDATE SET value = excluded.value, changed_at = excluded.changed_at, changed_by = excluded.changed_by;";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$value", value ? 1 : 0);
                command.Parameters.AddWithValue("$at", SqliteDatabase.Format(at));
                command.Parameters.AddWithValue("$actor", actor);
                command.ExecuteNonQuery();
                return true;
            });
        }
        catch (StorageUnavailableException)
        {
            return false;
        }
    }
}
