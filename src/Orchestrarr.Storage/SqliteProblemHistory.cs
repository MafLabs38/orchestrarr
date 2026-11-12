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

    public bool UndoSkip(long id, DateTimeOffset at) => SqliteDatabase.Guard(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE problem_history SET undone_at = $at WHERE id = $id AND state = 'Skipped' AND undone_at IS NULL;";
        command.Parameters.AddWithValue("$at", SqliteDatabase.Format(at));
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1;
    });

    public IReadOnlyList<ActiveSkip> ActiveSkips() => SqliteDatabase.Guard<IReadOnlyList<ActiveSkip>>(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT row_id, target FROM problem_history WHERE state = 'Skipped' AND undone_at IS NULL;";
        using var reader = command.ExecuteReader();
        var skips = new List<ActiveSkip>();
        while (reader.Read())
        {
            skips.Add(new ActiveSkip(reader.GetString(0), reader.GetString(1)));
        }
        return skips;
    });

    public IReadOnlyList<HistoryEntry> Page(HistoryState? state, int limit, long? before) => SqliteDatabase.Guard<IReadOnlyList<HistoryEntry>>(() =>
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
        var entries = new List<HistoryEntry>();
        while (reader.Read())
        {
            // A value that this version does not know (written by a newer one) is not a reason to lose the page: the line is left out
            if (!Enum.TryParse<HistoryState>(reader.GetString(3), out var rowState) || !Enum.TryParse<ProblemKind>(reader.GetString(5), out var kind))
            {
                continue;
            }
            entries.Add(new HistoryEntry(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), rowState, reader.GetString(4), kind, reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)), reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11)),
                SqliteDatabase.Parse(reader.GetString(12)), reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14),
                !reader.IsDBNull(15) && Enum.TryParse<HistoryOutcome>(reader.GetString(15), out var outcome) ? outcome : null));
        }
        return entries;
    });
}
