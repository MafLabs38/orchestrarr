using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Orchestrarr.Storage;

/// <summary>
/// The SQLite file that holds what must survive a restart: the approvals, the audit trail and the global stop (ADR 0010). One file, opened
/// for each operation; WAL so that a reader never blocks the writer. The schema is created here and versioned with <c>user_version</c>.
/// </summary>
public sealed class SqliteDatabase
{
    private const int SchemaVersion = 1;

    // A timestamp as text, in UTC with a fixed width, so that sorting the text sorts the time
    internal const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private readonly string _connectionString;

    private SqliteDatabase(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    public string Path { get; }

    /// <summary>Opens the file, creating its folder, the file and the schema when they are not there. Fails loudly when it cannot.</summary>
    public static SqliteDatabase Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("The path of the database is required.", nameof(path));
        }

        var full = System.IO.Path.GetFullPath(path);
        if (System.IO.Path.GetDirectoryName(full) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }

        var database = new SqliteDatabase(full);
        database.Migrate();
        return database;
    }

    internal SqliteConnection Connect()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    internal static string Format(DateTimeOffset moment) => moment.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    internal static DateTimeOffset Parse(string text) =>
        DateTimeOffset.ParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private void Migrate()
    {
        using var connection = Connect();
        Run(connection, "PRAGMA journal_mode = WAL;");
        var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException($"The database is of a newer version ({version}) than this program knows ({SchemaVersion}).");
        }
        if (version == SchemaVersion)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
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
            CREATE TRIGGER IF NOT EXISTS audit_is_append_only_update BEFORE UPDATE ON audit
              BEGIN SELECT RAISE(ABORT, 'the audit trail is append-only'); END;
            CREATE TRIGGER IF NOT EXISTS audit_is_append_only_delete BEFORE DELETE ON audit
              BEGIN SELECT RAISE(ABORT, 'the audit trail is append-only'); END;
            CREATE TABLE IF NOT EXISTS kill_switch (
              id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
              engaged INTEGER NOT NULL,
              changed_at TEXT NOT NULL
            );
            """);
        Run(connection, string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {SchemaVersion};"));
        transaction.Commit();
    }

    internal static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
