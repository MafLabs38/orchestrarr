using System.Globalization;
using Microsoft.Data.Sqlite;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Storage;

/// <summary>
/// The SQLite file that holds what must survive a restart: the approvals, the audit trail and the global stop (ADR 0013). One file, opened
/// for each operation; WAL so that a reader never blocks the writer. The schema is created here and versioned with <c>user_version</c>.
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    public static readonly TimeSpan DefaultBusyTimeout = TimeSpan.FromSeconds(5);

    // The version is the number of steps: one place to say it

    // The extended codes of a constraint, so that only a duplicate is read as "this decision is already there"
    private const int UniqueConstraint = 2067;
    private const int PrimaryKeyConstraint = 1555;

    // A timestamp as text, in UTC with a fixed width, so that sorting the text sorts the time
    internal const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private readonly string _connectionString;

    private SqliteDatabase(string path, TimeSpan busyTimeout)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            // No pool: a pooled connection keeps reading and writing a file that was deleted or replaced under the host, and nothing would say so
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = (int)busyTimeout.TotalSeconds < 1 ? 1 : (int)busyTimeout.TotalSeconds,
        }.ToString();
        BusyTimeoutMilliseconds = (int)busyTimeout.TotalMilliseconds;
    }

    /// <summary>Whether this call created the database: whatever history there was before is gone.</summary>
    public bool IsNew { get; private set; }

    private int BusyTimeoutMilliseconds { get; }

    /// <summary>
    /// Opens the file, creating its folder (for its owner only), the file and the schema when they are not there. Fails loudly when it cannot.
    /// A database that is created while <paramref name="startEngagedWhenNew"/> is set starts with the global stop engaged and a trace of it: an
    /// empty database may be a lost volume, and a stop that a lost volume releases does not stop.
    /// </summary>
    public static SqliteDatabase Open(string path, bool startEngagedWhenNew = false, TimeProvider? time = null, TimeSpan? busyTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("The path of the database is required.", nameof(path));
        }

        var full = System.IO.Path.GetFullPath(path);
        EnsureFolder(full);
        CreateEmptyFile(full);
        var database = new SqliteDatabase(full, busyTimeout ?? DefaultBusyTimeout);
        database.Migrate(startEngagedWhenNew, time ?? TimeProvider.System);
        return database;
    }

    /// <summary>Creates the folder of the database for its owner only, when it is not there. The lock of the one writer needs it too.</summary>
    internal static void EnsureFolder(string fullPath)
    {
        if (System.IO.Path.GetDirectoryName(fullPath) is not { Length: > 0 } folder)
        {
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
        }
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // The file is created here and only here, for its owner only: every later operation opens it read-write, so a file that vanishes while the
    // host runs is an error and not a new empty database that hides the loss.
    private static void CreateEmptyFile(string full)
    {
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using var created = new FileStream(full, options);
    }

    // The lock of the one writer, when this database was opened as the sole writer: it lives as long as the database does
    private SqliteWriterLock? _writerLock;

    /// <summary>
    /// Opens the database as the one writer of its file: the lock is taken first, before anything is created, migrated or written, and it is held
    /// until this database is disposed. A second host on the same file gets a <see cref="WriterLockException"/> and has touched nothing.
    /// </summary>
    public static SqliteDatabase OpenAsSoleWriter(string path, bool startEngagedWhenNew = false, TimeProvider? time = null, TimeSpan? busyTimeout = null)
    {
        var writerLock = SqliteWriterLock.Acquire(path);
        try
        {
            var database = Open(path, startEngagedWhenNew, time, busyTimeout);
            database._writerLock = writerLock;
            return database;
        }
        catch
        {
            writerLock.Dispose();
            throw;
        }
    }

    /// <summary>Lets go of the lock of the one writer, when this database holds one.</summary>
    public void Dispose() => _writerLock?.Dispose();

    internal SqliteConnection Connect()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // The wait for the write lock, in milliseconds: the connection string only knows whole seconds
        Run(connection, string.Create(CultureInfo.InvariantCulture, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};"));
        return connection;
    }

    /// <summary>Whether an error of SQLite says that the same decision is already there (a unique key), and not any other constraint.</summary>
    internal static bool IsDuplicate(SqliteException failure) => failure.SqliteExtendedErrorCode is UniqueConstraint or PrimaryKeyConstraint;

    /// <summary>
    /// Takes the write lock first (so that what was counted is still there when the rows go in), does the work, and commits or rolls back. A
    /// rollback that fails because SQLite already did it never hides the failure that came first.
    /// </summary>
    internal T InWriteTransaction<T>(Func<SqliteConnection, (bool Commit, T Result)> work)
    {
        using var connection = Connect();
        Run(connection, "BEGIN IMMEDIATE;");
        try
        {
            var (commit, result) = work(connection);
            Run(connection, commit ? "COMMIT;" : "ROLLBACK;");
            return result;
        }
        catch
        {
            TryRollback(connection);
            throw;
        }
    }

    /// <summary>What a file can do wrong: the storage cannot be used.</summary>
    internal static bool IsUnavailable(Exception failure) => failure is SqliteException or IOException;

    /// <summary>Turns what a file can do wrong into the one failure the product knows: the storage cannot be used.</summary>
    internal static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception failure) when (IsUnavailable(failure) || failure is FormatException or ArgumentException)
        {
            // A row that does not read back (a date, an identifier, a step that does not exist) is as unusable as a file that does not open
            throw new StorageUnavailableException("The storage cannot be used.", failure);
        }
    }

    internal static string Format(DateTimeOffset moment) => moment.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    internal static DateTimeOffset Parse(string text) =>
        DateTimeOffset.ParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    internal static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void TryRollback(SqliteConnection connection)
    {
        try
        {
            Run(connection, "ROLLBACK;");
        }
        catch (SqliteException)
        {
            // SQLite may have rolled back by itself (a full disk, an I/O error): what matters is the failure that came first
        }
    }

    // The schema as ordered steps: step n brings a database of version n to version n + 1, and each runs in the transaction that sets the version.
    // A new step is appended here, and SchemaVersion goes up with it.
    private delegate void Step(SqliteConnection connection, bool created, bool startEngagedWhenNew, TimeProvider time);

    private static readonly Step[] Steps = [CreateVersion1, CreateVersion2];

    private static int SchemaVersion => Steps.Length;

    private void Migrate(bool startEngagedWhenNew, TimeProvider time)
    {
        using var connection = Connect();
        Run(connection, "PRAGMA journal_mode = WAL;");
        var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException($"The database is of a newer version ({version}) than this program knows ({SchemaVersion}).");
        }
        IsNew = version == 0;
        for (var from = version; from < SchemaVersion; from++)
        {
            using var transaction = connection.BeginTransaction();
            Steps[from](connection, IsNew, startEngagedWhenNew, time);
            Run(connection, string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {from + 1};"));
            transaction.Commit();
        }
    }

    // Version 0 to 1: the tables, the triggers that keep the trail append-only and its counters, and the row of the stop
    private static void CreateVersion1(SqliteConnection connection, bool created, bool startEngagedWhenNew, TimeProvider time)
    {
        Run(connection, """
            CREATE TABLE IF NOT EXISTS approvals (
              id TEXT NOT NULL PRIMARY KEY,
              work_item_id TEXT NOT NULL,
              repository TEXT NOT NULL,
              pull_request INTEGER NOT NULL,
              head_sha TEXT NOT NULL,
              steps TEXT NOT NULL,
              approver TEXT NOT NULL,
              approved_at TEXT NOT NULL,
              step_up_at TEXT NULL,
              UNIQUE (repository, pull_request, head_sha, steps)
            );
            CREATE TABLE IF NOT EXISTS audit (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              at TEXT NOT NULL,
              event TEXT NOT NULL,
              subject TEXT NOT NULL,
              order_id TEXT NULL,
              detail TEXT NULL,
              refusal INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS audit_counters (
              id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
              refusals INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO audit_counters (id, refusals) VALUES (1, 0);
            CREATE TRIGGER IF NOT EXISTS audit_counts_refusals AFTER INSERT ON audit WHEN NEW.refusal = 1
              BEGIN UPDATE audit_counters SET refusals = refusals + 1 WHERE id = 1; END;
            CREATE TRIGGER IF NOT EXISTS audit_is_append_only_update BEFORE UPDATE ON audit
              BEGIN SELECT RAISE(ABORT, 'the audit trail is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS audit_is_append_only_delete BEFORE DELETE ON audit
              BEGIN SELECT RAISE(ABORT, 'the audit trail is append-only'); END;
            CREATE TABLE IF NOT EXISTS kill_switch (
              id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
              engaged INTEGER NOT NULL CHECK (engaged IN (0, 1))
            );
            """);
        // The row of the stop is always there: a row that is missing later is unknown, and unknown counts as engaged. An engaged stop that was
        // already there stays engaged.
        Run(connection, string.Create(CultureInfo.InvariantCulture,
            $"INSERT INTO kill_switch (id, engaged) VALUES (1, {(created && startEngagedWhenNew ? 1 : 0)}) ON CONFLICT (id) DO UPDATE SET engaged = MAX(engaged, excluded.engaged);"));
        if (created && startEngagedWhenNew)
        {
            SqliteAuditLog.Insert(connection, AuditEvents.InstanceCreatedEntry(time.GetUtcNow(), "database"));
        }
    }

    // Version 1 to 2 (ADR 0017): an approval has a life (waiting, taken, ended) and belongs to a batch of tasks. The unique key of a decision now holds
    // only while the approval is open, so a decision that ended may be approved again; SQLite cannot drop a table constraint, so the table is rebuilt.
    private static void CreateVersion2(SqliteConnection connection, bool created, bool startEngagedWhenNew, TimeProvider time)
    {
        // A file whose version was reset by hand may already hold the new shape: every statement here leaves what is already there
        if (Convert.ToInt32(Scalar(connection, "SELECT COUNT(*) FROM pragma_table_info('approvals') WHERE name = 'state';"), CultureInfo.InvariantCulture) == 0)
        {
            Run(connection, """
                CREATE TABLE approvals_v2 (
                  id TEXT NOT NULL PRIMARY KEY,
                  work_item_id TEXT NOT NULL,
                  repository TEXT NOT NULL,
                  pull_request INTEGER NOT NULL,
                  head_sha TEXT NOT NULL,
                  steps TEXT NOT NULL,
                  approver TEXT NOT NULL,
                  approved_at TEXT NOT NULL,
                  step_up_at TEXT NULL,
                  state TEXT NOT NULL DEFAULT 'Waiting' CHECK (state IN ('Waiting', 'Taken', 'DryRunDone', 'Refused', 'Expired')),
                  batch_id TEXT NULL,
                  ended_at TEXT NULL
                );
                INSERT INTO approvals_v2 (id, work_item_id, repository, pull_request, head_sha, steps, approver, approved_at, step_up_at)
                  SELECT id, work_item_id, repository, pull_request, head_sha, steps, approver, approved_at, step_up_at FROM approvals;
                DROP TABLE approvals;
                ALTER TABLE approvals_v2 RENAME TO approvals;
                """);
        }
        Run(connection, """
            CREATE UNIQUE INDEX IF NOT EXISTS approvals_open_decision ON approvals (repository, pull_request, head_sha, steps) WHERE state IN ('Waiting', 'Taken');
            CREATE INDEX IF NOT EXISTS approvals_by_batch ON approvals (batch_id);
            CREATE TABLE IF NOT EXISTS batches (
              id TEXT NOT NULL PRIMARY KEY,
              work_item_id TEXT NOT NULL,
              approver TEXT NOT NULL,
              created_at TEXT NOT NULL,
              expires_at TEXT NOT NULL,
              delegation_id TEXT NULL,
              digest TEXT NOT NULL,
              state TEXT NOT NULL CHECK (state IN ('Waiting', 'Delivered', 'Done', 'Refused', 'Expired')),
              code TEXT NULL,
              executor TEXT NULL,
              delivered_at TEXT NULL,
              ended_at TEXT NULL,
              refusal TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS batches_by_state ON batches (state, created_at);
            CREATE TABLE IF NOT EXISTS tasks (
              id TEXT NOT NULL PRIMARY KEY,
              batch_id TEXT NOT NULL REFERENCES batches (id),
              sequence INTEGER NOT NULL,
              kind TEXT NOT NULL,
              repository TEXT NOT NULL,
              pull_request INTEGER NOT NULL,
              head_sha TEXT NOT NULL,
              approval_id TEXT NOT NULL,
              result TEXT NULL,
              reason TEXT NULL,
              UNIQUE (batch_id, sequence)
            );
            CREATE TABLE IF NOT EXISTS run_events (
              batch_id TEXT NOT NULL REFERENCES batches (id),
              sequence INTEGER NOT NULL,
              at TEXT NOT NULL,
              type TEXT NOT NULL,
              task_id TEXT NULL,
              detail TEXT NULL,
              previous_hash TEXT NOT NULL,
              hash TEXT NOT NULL,
              PRIMARY KEY (batch_id, sequence)
            );
            CREATE TRIGGER IF NOT EXISTS run_events_are_append_only_update BEFORE UPDATE ON run_events
              BEGIN SELECT RAISE(ABORT, 'the trace of a run is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS run_events_are_append_only_delete BEFORE DELETE ON run_events
              BEGIN SELECT RAISE(ABORT, 'the trace of a run is append-only'); END;
            """);
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
