using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Storage;

/// <summary>
/// The audit trail in the SQLite file: append-only (the database itself refuses an update or a delete) and bounded. Past its capacity it
/// refuses, so that nothing happens without a trace (SPEC D-43, F-SAF-02); refusals have a smaller share than the rest, as in memory, so that a
/// client that is refused again and again cannot use the trail up. An entry that cannot be written is reported as not recorded, never lost silently.
/// </summary>
public sealed class SqliteAuditLog(SqliteDatabase database, int capacity = InMemoryAuditLog.DefaultCapacity, ILogger<SqliteAuditLog>? logger = null) : IAuditLog
{
    /// <summary>The share of the trail that refusals may use, in quarters.</summary>
    private const int RefusalQuarters = 3;

    private readonly int _capacity = int.IsNegative(capacity) ? throw new ArgumentOutOfRangeException(nameof(capacity), "The capacity cannot be negative.") : capacity;
    private readonly int _refusalLimit = (int)(capacity * (long)RefusalQuarters / 4);
    private readonly ILogger _logger = logger ?? NullLogger<SqliteAuditLog>.Instance;

    public ValueTask<bool> TryRecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return TryRecordRangeAsync([entry], cancellationToken);
    }

    public ValueTask<bool> TryRecordRangeAsync(IReadOnlyList<AuditEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        try
        {
            return ValueTask.FromResult(Write(entries));
        }
        catch (Exception failure) when (failure is SqliteException or IOException or InvalidOperationException)
        {
            _logger.LogError(failure, "The audit trail cannot be written, so nothing is recorded");
            return ValueTask.FromResult(false);
        }
    }

    public IReadOnlyList<AuditEntry> List()
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT at, event, subject, order_id, detail FROM audit ORDER BY id DESC;";
        using var reader = command.ExecuteReader();
        var entries = new List<AuditEntry>();
        while (reader.Read())
        {
            entries.Add(new AuditEntry(
                SqliteDatabase.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return entries;
    }

    private bool Write(IReadOnlyList<AuditEntry> entries)
    {
        using var connection = database.Connect();
        SqliteDatabase.Run(connection, "BEGIN IMMEDIATE;");
        try
        {
            var total = Count(connection, "SELECT COUNT(*) FROM audit;");
            var refusalsHere = entries.Count(entry => AuditEvents.IsRefusal(entry.Event));
            var fits = total + entries.Count <= _capacity
                && (refusalsHere == 0 || Count(connection, "SELECT COUNT(*) FROM audit WHERE refusal = 1;") + refusalsHere <= _refusalLimit);
            if (!fits)
            {
                SqliteDatabase.Run(connection, "ROLLBACK;");
                return false;
            }
            foreach (var entry in entries)
            {
                Insert(connection, entry);
            }
            SqliteDatabase.Run(connection, "COMMIT;");
            return true;
        }
        catch
        {
            SqliteDatabase.Run(connection, "ROLLBACK;");
            throw;
        }
    }

    private static int Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Insert(SqliteConnection connection, AuditEntry entry)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO audit (at, event, subject, order_id, detail, refusal) VALUES ($at, $event, $subject, $order, $detail, $refusal);";
        command.Parameters.AddWithValue("$at", SqliteDatabase.Format(entry.At));
        command.Parameters.AddWithValue("$event", entry.Event);
        command.Parameters.AddWithValue("$subject", entry.Subject);
        command.Parameters.AddWithValue("$order", entry.OrderId is { } order ? order : DBNull.Value);
        command.Parameters.AddWithValue("$detail", entry.Detail is { } detail ? detail : DBNull.Value);
        command.Parameters.AddWithValue("$refusal", AuditEvents.IsRefusal(entry.Event) ? 1 : 0);
        command.ExecuteNonQuery();
    }
}
