using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Storage;

/// <summary>
/// The stores of the missions, the mandates, the problems and the dossiers in the SQLite file. Each entity is a document kept under the key it is looked up by; the
/// domain decides what a valid change is, the store only remembers it. A read or a write that fails raises the unavailable failure, like the other stores.
/// </summary>
internal static class MissionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Read<T>(object? body) where T : class => body is string text ? JsonSerializer.Deserialize<T>(text, Options) : null;
}

public sealed class SqliteMissionStore(SqliteDatabase database) : IMissionStore
{
    public Mission? Get(string id) => SqliteDatabase.Guard(() => MissionJson.Read<Mission>(Scalar(database, "SELECT body FROM missions WHERE id = $key;", id)));

    public void Save(Mission mission)
    {
        ArgumentNullException.ThrowIfNull(mission);
        SqliteDatabase.Guard(() =>
        {
            using var connection = database.Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO missions (id, problem_id, body) VALUES ($id, $problem, $body) ON CONFLICT(id) DO UPDATE SET body = excluded.body;";
            command.Parameters.AddWithValue("$id", mission.Id);
            command.Parameters.AddWithValue("$problem", mission.ProblemId);
            command.Parameters.AddWithValue("$body", MissionJson.Write(mission));
            command.ExecuteNonQuery();
            return true;
        });
    }

    public IReadOnlyList<Mission> ForProblem(string problemId) => SqliteDatabase.Guard(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM missions WHERE problem_id = $problem ORDER BY id;";
        command.Parameters.AddWithValue("$problem", problemId);
        return Bodies<Mission>(command);
    });

    internal static object? Scalar(SqliteDatabase database, string sql, string key)
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar();
    }

    internal static IReadOnlyList<T> Bodies<T>(SqliteCommand command) where T : class
    {
        var list = new List<T>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (MissionJson.Read<T>(reader.GetString(0)) is { } value)
            {
                list.Add(value);
            }
        }
        return list;
    }
}

public sealed class SqliteMandateLedger(SqliteDatabase database) : IMandateLedger
{
    public void Record(SignedMandate mandate)
    {
        ArgumentNullException.ThrowIfNull(mandate);
        SqliteDatabase.Guard(() =>
        {
            using var connection = database.Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO mandates (id, problem_id, body) VALUES ($id, $problem, $body) ON CONFLICT(id) DO NOTHING;";
            command.Parameters.AddWithValue("$id", mandate.Mandate.Id);
            command.Parameters.AddWithValue("$problem", mandate.Mandate.ProblemId);
            command.Parameters.AddWithValue("$body", MissionJson.Write(mandate));
            command.ExecuteNonQuery();
            return true;
        });
    }

    public SignedMandate? Get(string id) => SqliteDatabase.Guard(() => MissionJson.Read<SignedMandate>(SqliteMissionStore.Scalar(database, "SELECT body FROM mandates WHERE id = $key;", id)));

    public int CountForProblem(string problemId) => SqliteDatabase.Guard(() => Convert.ToInt32(SqliteMissionStore.Scalar(database, "SELECT COUNT(*) FROM mandates WHERE problem_id = $key;", problemId), System.Globalization.CultureInfo.InvariantCulture));

    public void Revoke(string id) => SqliteDatabase.Guard(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE mandates SET revoked = 1 WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        return true;
    });

    public bool IsRevoked(string id) => SqliteDatabase.Guard(() => Convert.ToInt32(SqliteMissionStore.Scalar(database, "SELECT revoked FROM mandates WHERE id = $key;", id), System.Globalization.CultureInfo.InvariantCulture) == 1);
}

public sealed class SqliteProblemStore(SqliteDatabase database) : IProblemStore
{
    public ProblemRun? Get(string problemId) => SqliteDatabase.Guard(() => MissionJson.Read<ProblemRun>(SqliteMissionStore.Scalar(database, "SELECT body FROM problems WHERE problem_id = $key;", problemId)));

    public void Save(ProblemRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        SqliteDatabase.Guard(() =>
        {
            using var connection = database.Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO problems (problem_id, body) VALUES ($id, $body) ON CONFLICT(problem_id) DO UPDATE SET body = excluded.body;";
            command.Parameters.AddWithValue("$id", run.ProblemId);
            command.Parameters.AddWithValue("$body", MissionJson.Write(run));
            command.ExecuteNonQuery();
            return true;
        });
    }
}

public sealed class SqliteDossierStore(SqliteDatabase database) : IDossierStore
{
    public Dossier? Get(string missionId) => SqliteDatabase.Guard(() => MissionJson.Read<Dossier>(SqliteMissionStore.Scalar(database, "SELECT body FROM dossiers WHERE mission_id = $key;", missionId)));

    public void Save(Dossier dossier)
    {
        ArgumentNullException.ThrowIfNull(dossier);
        SqliteDatabase.Guard(() =>
        {
            using var connection = database.Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO dossiers (mission_id, body) VALUES ($id, $body) ON CONFLICT(mission_id) DO UPDATE SET body = excluded.body;";
            command.Parameters.AddWithValue("$id", dossier.MissionId);
            command.Parameters.AddWithValue("$body", MissionJson.Write(dossier));
            command.ExecuteNonQuery();
            return true;
        });
    }
}
