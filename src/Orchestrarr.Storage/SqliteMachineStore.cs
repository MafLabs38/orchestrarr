using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Machines;

namespace Orchestrarr.Storage;

/// <summary>The lab machines and their transitions in the SQLite file of the control plane (ADR 0047): the state survives a restart, and the history is kept for the metrics.</summary>
public sealed class SqliteMachineStore(SqliteDatabase database) : IMachineStore
{
    public Machine? Get(string id) => SqliteDatabase.Guard(() => MissionJson.Read<Machine>(SqliteMissionStore.Scalar(database, "SELECT body FROM machines WHERE id = $key;", id)));

    public IReadOnlyList<Machine> All() => SqliteDatabase.Guard(() =>
    {
        using var connection = database.ConnectShared();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM machines ORDER BY id;";
        return Read<Machine>(command);
    });

    public void Save(Machine machine, MachineTransition? transition)
    {
        ArgumentNullException.ThrowIfNull(machine);
        SqliteDatabase.Guard(() =>
        {
            // The state and its transition go in together, or neither does
            database.Atomically(() =>
            {
                using var connection = database.ConnectShared();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO machines (id, body) VALUES ($id, $body) ON CONFLICT(id) DO UPDATE SET body = excluded.body;";
                    command.Parameters.AddWithValue("$id", machine.Id);
                    command.Parameters.AddWithValue("$body", MissionJson.Write(machine));
                    command.ExecuteNonQuery();
                }
                if (transition is not null)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "INSERT INTO machine_transitions (machine_id, at, body) VALUES ($machine, $at, $body);";
                    command.Parameters.AddWithValue("$machine", transition.MachineId);
                    command.Parameters.AddWithValue("$at", transition.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                    command.Parameters.AddWithValue("$body", MissionJson.Write(transition));
                    command.ExecuteNonQuery();
                }
            });
            return true;
        });
    }

    public IReadOnlyList<MachineTransition> Transitions(string id) => SqliteDatabase.Guard(() =>
    {
        using var connection = database.ConnectShared();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM machine_transitions WHERE machine_id = $machine ORDER BY id;";
        command.Parameters.AddWithValue("$machine", id);
        return Read<MachineTransition>(command);
    });

    private static List<T> Read<T>(Microsoft.Data.Sqlite.SqliteCommand command)
        where T : class
    {
        var list = new List<T>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(MissionJson.Read<T>(reader.GetString(0)) ?? throw new StorageUnavailableException("a stored document of the machines cannot be read"));
        }
        return list;
    }
}
