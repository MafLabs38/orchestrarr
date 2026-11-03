using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Storage;

/// <summary>
/// The audit trail in the SQLite file: append-only (the database itself refuses an update or a delete) and bounded. Past its capacity it
/// refuses, so that nothing happens without a trace (SPEC D-43, F-SAF-02); refusals have only a share of it (<see cref="AuditCapacityPolicy"/>,
/// the same rule as in memory). An entry that cannot be written is reported as not recorded, never lost silently.
/// </summary>
public sealed class SqliteAuditLog(SqliteDatabase database, int capacity = StoreLimits.FileAudit, ILogger<SqliteAuditLog>? logger = null) : IAuditLog
{
    private readonly int _capacity = int.IsNegative(capacity) ? throw new ArgumentOutOfRangeException(nameof(capacity), "The capacity cannot be negative.") : capacity;
    private readonly ILogger _logger = logger ?? NullLogger<SqliteAuditLog>.Instance;

    public ValueTask<bool> TryRecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return TryRecordRangeAsync([entry], cancellationToken);
    }

    public ValueTask<bool> TryRecordRangeAsync(IReadOnlyList<AuditEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return ValueTask.FromResult(database.InWriteTransaction(connection =>
            {
                var refusals = entries.Count(entry => AuditEvents.IsRefusal(entry.Event));
                if (!AuditCapacityPolicy.Fits(_capacity, Count(connection), RefusalCount(connection), entries.Count, refusals))
                {
                    return (false, false);
                }
                foreach (var entry in entries)
                {
                    Insert(connection, entry);
                }
                return (true, true);
            }));
        }
        catch (Exception failure) when (failure is SqliteException or IOException or InvalidOperationException)
        {
            _logger.LogError(failure, "The audit trail cannot be written, so nothing is recorded");
            return ValueTask.FromResult(false);
        }
    }

    public IReadOnlyList<AuditEntry> List() => SqliteDatabase.Guard<IReadOnlyList<AuditEntry>>(() =>
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
    });

    internal static int Count(SqliteConnection connection) => Scalar(connection, "SELECT COUNT(*) FROM audit;");

    internal static int RefusalCount(SqliteConnection connection) => Scalar(connection, "SELECT COUNT(*) FROM audit WHERE refusal = 1;");

    internal static void Insert(SqliteConnection connection, AuditEntry entry)
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

    private static int Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
