using Orchestrarr.Core.Decisions;

namespace Orchestrarr.Storage;

/// <summary>The history of the decisions about the problems, in the SQLite file (ADR 0027). A read or a write that fails raises the unavailable failure.</summary>
public sealed class SqliteProblemHistory(SqliteDatabase database) : IProblemHistory
{
    public long Record(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return SqliteDatabase.Guard(() =>
        {
            using var connection = database.Connect();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO problem_history (row_id, target, state, application, kind, description, repository, pull_request, head_sha, approval_id, batch_id, at, by, reason)
                VALUES ($row, $target, $state, $application, $kind, $description, $repository, $pr, $sha, $approval, $batch, $at, $by, $reason);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$row", entry.RowId);
            command.Parameters.AddWithValue("$target", entry.Target);
            command.Parameters.AddWithValue("$state", entry.State.ToString());
            command.Parameters.AddWithValue("$application", entry.Application);
            command.Parameters.AddWithValue("$kind", entry.Kind.ToString());
            command.Parameters.AddWithValue("$description", entry.Description);
            command.Parameters.AddWithValue("$repository", (object?)entry.Repository ?? DBNull.Value);
            command.Parameters.AddWithValue("$pr", (object?)entry.PullRequest ?? DBNull.Value);
            command.Parameters.AddWithValue("$sha", (object?)entry.HeadSha ?? DBNull.Value);
            command.Parameters.AddWithValue("$approval", entry.ApprovalId is { } approval ? approval.ToString("D") : DBNull.Value);
            command.Parameters.AddWithValue("$batch", entry.BatchId is { } batch ? batch.ToString("D") : DBNull.Value);
            command.Parameters.AddWithValue("$at", SqliteDatabase.Format(entry.At));
            command.Parameters.AddWithValue("$by", entry.By);
            command.Parameters.AddWithValue("$reason", (object?)entry.Reason ?? DBNull.Value);
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        });
    }

    public void Conclude(long id, HistoryOutcome outcome) => SqliteDatabase.Guard(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE problem_history SET outcome = $outcome WHERE id = $id AND state = 'Approved' AND outcome IS NULL;";
        command.Parameters.AddWithValue("$outcome", outcome.ToString());
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        return true;
    });

    public int LapseSkips(IReadOnlyCollection<ActiveSkip> skips) => SqliteDatabase.Guard(() =>
    {
        var ended = 0;
        using var connection = database.Connect();
        foreach (var skip in skips)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE problem_history SET outcome = 'Ended' WHERE state = 'Skipped' AND undone_at IS NULL AND outcome IS NULL AND row_id = $row AND target = $target;";
            command.Parameters.AddWithValue("$row", skip.RowId);
            command.Parameters.AddWithValue("$target", skip.Target);
            ended += command.ExecuteNonQuery();
        }
        return ended;
    });

    public bool UndoSkip(long id, DateTimeOffset at) => SqliteDatabase.Guard(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE problem_history SET undone_at = $at WHERE id = $id AND state = 'Skipped' AND undone_at IS NULL AND outcome IS NULL;";
        command.Parameters.AddWithValue("$at", SqliteDatabase.Format(at));
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1;
    });

    public IReadOnlyList<ActiveSkip> ActiveSkips() => SqliteDatabase.Guard<IReadOnlyList<ActiveSkip>>(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT row_id, target FROM problem_history WHERE state = 'Skipped' AND undone_at IS NULL AND outcome IS NULL;";
        using var reader = command.ExecuteReader();
        var skips = new List<ActiveSkip>();
        while (reader.Read())
        {
            skips.Add(new ActiveSkip(reader.GetString(0), reader.GetString(1)));
        }
        return skips;
    });

    public HistoryScan ReadPage(HistoryState? state, int limit, long? before) => SqliteDatabase.Guard(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, row_id, target, state, application, kind, description, repository, pull_request, head_sha, approval_id, batch_id, at, by, reason, outcome
            FROM problem_history
            WHERE undone_at IS NULL AND ($state IS NULL OR state = $state) AND ($before IS NULL OR id < $before)
            ORDER BY id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$state", state is { } s ? s.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("$before", before is { } b ? b : DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        return Read(reader, limit);
    });

    public HistoryScan ReadUnconcluded(int limit, long after) => SqliteDatabase.Guard(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, row_id, target, state, application, kind, description, repository, pull_request, head_sha, approval_id, batch_id, at, by, reason, outcome
            FROM problem_history
            WHERE undone_at IS NULL AND state = 'Approved' AND outcome IS NULL AND id > $after
            ORDER BY id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$after", after);
        using var reader = command.ExecuteReader();
        return Read(reader, limit);
    });

    // A name of the enumeration, never a number
    private static bool TryParse<T>(string text, out T value)
        where T : struct, Enum => Enum.TryParse(text, out value) && Enum.IsDefined(value);

    private static HistoryScan Read(Microsoft.Data.Sqlite.SqliteDataReader reader, int limit)
    {
        var entries = new List<HistoryEntry>();
        var scanned = 0;
        long last = 0;
        while (reader.Read())
        {
            scanned++;
            last = reader.GetInt64(0);
            // A value that this version does not know (written by a newer one) is not a reason to lose the page: the line is left out
            if (!TryParse<HistoryState>(reader.GetString(3), out var rowState) || !TryParse<ProblemKind>(reader.GetString(5), out var kind))
            {
                continue;
            }
            try
            {
                entries.Add(new HistoryEntry(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2), rowState, reader.GetString(4), kind, reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)), reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11)),
                    SqliteDatabase.Parse(reader.GetString(12)), reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14),
                    !reader.IsDBNull(15) && TryParse<HistoryOutcome>(reader.GetString(15), out var outcome) ? outcome : null));
            }
            catch (FormatException)
            {
                // A line that cannot be read (an identifier or a date that is not one) is left out like one of a newer version: it must not stop the page, or the pass that concludes
            }
        }
        return new HistoryScan(entries, scanned == limit ? last : null);
    }
}
