using System.Globalization;
using Microsoft.Data.Sqlite;
using Orchestrarr.Core.SecondFactor;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor;

/// <summary>A standing delegation, as kept by the executor. It stands in for the code of the second factor within a narrow frame.</summary>
public sealed record Delegation(Guid Id, string Subject, TaskKind Kind, string Repository, DateTimeOffset ExpiresAt, int DailyQuota, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt)
{
    public DelegationInfo ToInfo() => new(Id, Subject, Kind, Repository, ExpiresAt, DailyQuota);
}

/// <summary>
/// The executor's own file (ADR 0017). It holds what the control plane must not be able to write: the secrets of the operators, the last code
/// accepted for each, the batches already run, the standing delegations, and the trace of every run. It is also its own source of truth for
/// "never run twice". The file is created for its owner only; the secrets in it are as protected as the file is.
/// </summary>
public sealed class ExecutorStore : ISecondFactorSecrets, ISecondFactorUseLedger
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private readonly string _connectionString;

    public ExecutorStore(string path)
    {
        var full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) is { Length: > 0 } folder)
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(folder);
            }
            else
            {
                Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        // The file is created here, for its owner only, before anything opens it
        new FileStream(full, options).Dispose();
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 10 }.ToString();
        Migrate();
    }

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Run(connection, "PRAGMA busy_timeout = 10000;");
        return connection;
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void Migrate()
    {
        using var connection = Connect();
        Run(connection, "PRAGMA journal_mode = WAL;");
        Run(connection, """
            CREATE TABLE IF NOT EXISTS operators (
              subject TEXT NOT NULL PRIMARY KEY,
              secret BLOB NOT NULL,
              enrolled_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS second_factor_steps (
              subject TEXT NOT NULL PRIMARY KEY,
              last_step INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS executed_batches (
              batch_id TEXT NOT NULL PRIMARY KEY,
              at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS delegations (
              id TEXT NOT NULL PRIMARY KEY,
              subject TEXT NOT NULL,
              kind TEXT NOT NULL,
              repository TEXT NOT NULL,
              expires_at TEXT NOT NULL,
              daily_quota INTEGER NOT NULL,
              created_at TEXT NOT NULL,
              revoked_at TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS delegation_uses (
              delegation_id TEXT NOT NULL,
              day TEXT NOT NULL,
              used INTEGER NOT NULL,
              PRIMARY KEY (delegation_id, day)
            );
            CREATE TABLE IF NOT EXISTS run_events (
              run_id TEXT NOT NULL,
              batch_id TEXT NOT NULL,
              sequence INTEGER NOT NULL,
              at TEXT NOT NULL,
              type TEXT NOT NULL,
              task_id TEXT NULL,
              detail TEXT NULL,
              previous_hash TEXT NOT NULL,
              hash TEXT NOT NULL,
              PRIMARY KEY (run_id, sequence)
            );
            CREATE INDEX IF NOT EXISTS run_events_by_batch ON run_events (batch_id);
            CREATE TRIGGER IF NOT EXISTS run_events_are_append_only_update BEFORE UPDATE ON run_events
              BEGIN SELECT RAISE(ABORT, 'the trace of a run is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS run_events_are_append_only_delete BEFORE DELETE ON run_events
              BEGIN SELECT RAISE(ABORT, 'the trace of a run is append-only'); END;
            """);
    }

    private static string Format(DateTimeOffset moment) => moment.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string text) =>
        DateTimeOffset.ParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    // --- the second factor

    public ValueTask<byte[]?> FindAsync(string subject, CancellationToken cancellationToken)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT secret FROM operators WHERE subject = $subject;";
        command.Parameters.AddWithValue("$subject", subject);
        return ValueTask.FromResult(command.ExecuteScalar() as byte[]);
    }

    /// <summary>Enrols an operator, or replaces their secret. <paramref name="lastStep"/> is the code that confirmed it, so that it is not good again.</summary>
    public void Enrol(string subject, byte[] secret, long lastStep, DateTimeOffset at)
    {
        using var connection = Connect();
        Run(connection, "BEGIN IMMEDIATE;");
        try
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO operators (subject, secret, enrolled_at) VALUES ($subject, $secret, $at) ON CONFLICT (subject) DO UPDATE SET secret = excluded.secret, enrolled_at = excluded.enrolled_at;";
                command.Parameters.AddWithValue("$subject", subject);
                command.Parameters.AddWithValue("$secret", secret);
                command.Parameters.AddWithValue("$at", Format(at));
                command.ExecuteNonQuery();
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO second_factor_steps (subject, last_step) VALUES ($subject, $step) ON CONFLICT (subject) DO UPDATE SET last_step = excluded.last_step;";
                command.Parameters.AddWithValue("$subject", subject);
                command.Parameters.AddWithValue("$step", lastStep);
                command.ExecuteNonQuery();
            }
            Run(connection, "COMMIT;");
        }
        catch
        {
            Run(connection, "ROLLBACK;");
            throw;
        }
    }

    public IReadOnlyList<(string Subject, DateTimeOffset EnrolledAt)> Operators()
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT subject, enrolled_at FROM operators ORDER BY subject;";
        using var reader = command.ExecuteReader();
        var operators = new List<(string, DateTimeOffset)>();
        while (reader.Read())
        {
            operators.Add((reader.GetString(0), Parse(reader.GetString(1))));
        }
        return operators;
    }

    public bool Remove(string subject)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM operators WHERE subject = $subject;";
        command.Parameters.AddWithValue("$subject", subject);
        return command.ExecuteNonQuery() > 0;
    }

    public ValueTask<bool> TryAdvanceAsync(string subject, long step, CancellationToken cancellationToken)
    {
        using var connection = Connect();
        // One statement decides: the step is recorded only if it is later than the last one, so two requests with one code never both succeed
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO second_factor_steps (subject, last_step) VALUES ($subject, $step)
            ON CONFLICT (subject) DO UPDATE SET last_step = excluded.last_step WHERE excluded.last_step > second_factor_steps.last_step;
            """;
        command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$step", step);
        return ValueTask.FromResult(command.ExecuteNonQuery() > 0);
    }

    // --- never run twice

    /// <summary>True the first time a batch is seen, false after. The batch is written down before anything is checked about its authorization.</summary>
    public bool TryRegisterBatch(Guid batchId, DateTimeOffset at)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO executed_batches (batch_id, at) VALUES ($id, $at);";
        command.Parameters.AddWithValue("$id", batchId.ToString("D"));
        command.Parameters.AddWithValue("$at", Format(at));
        return command.ExecuteNonQuery() > 0;
    }

    // --- standing delegations

    public void AddDelegation(Delegation delegation)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO delegations (id, subject, kind, repository, expires_at, daily_quota, created_at) VALUES ($id, $subject, $kind, $repository, $expires, $quota, $created);";
        command.Parameters.AddWithValue("$id", delegation.Id.ToString("D"));
        command.Parameters.AddWithValue("$subject", delegation.Subject);
        command.Parameters.AddWithValue("$kind", delegation.Kind.ToString());
        command.Parameters.AddWithValue("$repository", delegation.Repository);
        command.Parameters.AddWithValue("$expires", Format(delegation.ExpiresAt));
        command.Parameters.AddWithValue("$quota", delegation.DailyQuota);
        command.Parameters.AddWithValue("$created", Format(delegation.CreatedAt));
        command.ExecuteNonQuery();
    }

    public bool RevokeDelegation(Guid id, DateTimeOffset at)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE delegations SET revoked_at = $at WHERE id = $id AND revoked_at IS NULL;";
        command.Parameters.AddWithValue("$at", Format(at));
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return command.ExecuteNonQuery() > 0;
    }

    public Delegation? FindDelegation(Guid id) => Delegations(includeEnded: true).FirstOrDefault(delegation => delegation.Id == id);

    /// <summary>The delegations, those still in force first. With <paramref name="includeEnded"/> the revoked and the expired too.</summary>
    public IReadOnlyList<Delegation> Delegations(bool includeEnded, DateTimeOffset? now = null)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, subject, kind, repository, expires_at, daily_quota, created_at, revoked_at FROM delegations ORDER BY created_at DESC;";
        using var reader = command.ExecuteReader();
        var all = new List<Delegation>();
        while (reader.Read())
        {
            all.Add(new Delegation(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                Enum.Parse<TaskKind>(reader.GetString(2)),
                reader.GetString(3),
                Parse(reader.GetString(4)),
                reader.GetInt32(5),
                Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : Parse(reader.GetString(7))));
        }
        return includeEnded ? all : [.. all.Where(delegation => delegation.RevokedAt is null && delegation.ExpiresAt > (now ?? DateTimeOffset.UtcNow))];
    }

    /// <summary>Counts <paramref name="count"/> uses on the day of <paramref name="now"/>, if the quota allows it. All or nothing.</summary>
    public bool TryUseDelegation(Guid id, int quota, int count, DateTimeOffset now)
    {
        var day = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var connection = Connect();
        Run(connection, "BEGIN IMMEDIATE;");
        try
        {
            int used;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT used FROM delegation_uses WHERE delegation_id = $id AND day = $day;";
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                command.Parameters.AddWithValue("$day", day);
                used = command.ExecuteScalar() is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;
            }
            if (used + count > quota)
            {
                Run(connection, "ROLLBACK;");
                return false;
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO delegation_uses (delegation_id, day, used) VALUES ($id, $day, $used) ON CONFLICT (delegation_id, day) DO UPDATE SET used = excluded.used;";
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                command.Parameters.AddWithValue("$day", day);
                command.Parameters.AddWithValue("$used", used + count);
                command.ExecuteNonQuery();
            }
            Run(connection, "COMMIT;");
            return true;
        }
        catch
        {
            Run(connection, "ROLLBACK;");
            throw;
        }
    }

    // --- the local trace

    /// <summary>
    /// Keeps a line of the trace of a run in the executor's own file, before it is sent anywhere. A run has an identifier of its own: the same
    /// batch seen twice (a replay) leaves two runs, and the second is the proof of it.
    /// </summary>
    public void AppendTrace(Guid runId, Guid batchId, RunEvent line)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO run_events (run_id, batch_id, sequence, at, type, task_id, detail, previous_hash, hash) VALUES ($run, $batch, $sequence, $at, $type, $task, $detail, $previous, $hash);";
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        command.Parameters.AddWithValue("$sequence", line.Sequence);
        command.Parameters.AddWithValue("$at", Format(line.At));
        command.Parameters.AddWithValue("$type", line.Type);
        command.Parameters.AddWithValue("$task", line.TaskId is { } task ? task.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$detail", line.Detail is null ? DBNull.Value : line.Detail);
        command.Parameters.AddWithValue("$previous", line.PreviousHash);
        command.Parameters.AddWithValue("$hash", line.Hash);
        command.ExecuteNonQuery();
    }

    /// <summary>The trace of the latest run of this batch.</summary>
    public IReadOnlyList<RunEvent> Trace(Guid batchId)
    {
        using var connection = Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sequence, at, type, task_id, detail, previous_hash, hash FROM run_events WHERE run_id = (SELECT run_id FROM run_events WHERE batch_id = $batch ORDER BY rowid DESC LIMIT 1) ORDER BY sequence;";
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        using var reader = command.ExecuteReader();
        var lines = new List<RunEvent>();
        while (reader.Read())
        {
            lines.Add(new RunEvent(reader.GetInt64(0), Parse(reader.GetString(1)), reader.GetString(2), reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        }
        return lines;
    }
}
