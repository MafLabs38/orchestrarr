using System.Globalization;
using Microsoft.Data.Sqlite;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Storage;

/// <summary>
/// The queue of batches and the trace of the runs, kept in the SQLite file (ADR 0017). Each change of state is one transaction together with
/// the state of the approvals it moves and its entry in the audit trail, so that none of the three can say something the others do not.
/// A batch is handed out once; the code of the second factor leaves the file with it.
/// </summary>
public sealed class SqliteBatchQueue(SqliteDatabase database, IKillSwitch killSwitch, TimeProvider time, TaskOptions options, int auditCapacity = StoreLimits.FileAudit) : IBatchQueue
{
    /// <summary>Writes a batch and its tasks, in the transaction of <paramref name="connection"/>. The batch is Waiting, with the code it waits to hand over.</summary>
    internal static void InsertBatch(SqliteConnection connection, TaskBatch batch, string? code)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO batches (id, work_item_id, approver, created_at, expires_at, delegation_id, digest, state, code) VALUES ($id, $workItem, $approver, $createdAt, $expiresAt, $delegation, $digest, 'Waiting', $code);";
            command.Parameters.AddWithValue("$id", batch.Id.ToString("D"));
            command.Parameters.AddWithValue("$workItem", batch.WorkItemId);
            command.Parameters.AddWithValue("$approver", batch.ApproverSubject);
            command.Parameters.AddWithValue("$createdAt", SqliteDatabase.Format(batch.CreatedAt));
            command.Parameters.AddWithValue("$expiresAt", SqliteDatabase.Format(batch.ExpiresAt));
            command.Parameters.AddWithValue("$delegation", batch.DelegationId is { } delegation ? delegation.ToString("D") : DBNull.Value);
            command.Parameters.AddWithValue("$digest", batch.Digest());
            command.Parameters.AddWithValue("$code", code is null ? DBNull.Value : code);
            command.ExecuteNonQuery();
        }
        foreach (var task in batch.Tasks)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO tasks (id, batch_id, sequence, kind, repository, pull_request, head_sha, approval_id) VALUES ($id, $batch, $sequence, $kind, $repository, $pullRequest, $head, $approval);";
            command.Parameters.AddWithValue("$id", task.Id.ToString("D"));
            command.Parameters.AddWithValue("$batch", batch.Id.ToString("D"));
            command.Parameters.AddWithValue("$sequence", task.Sequence);
            command.Parameters.AddWithValue("$kind", task.Kind.ToString());
            command.Parameters.AddWithValue("$repository", task.Repository);
            command.Parameters.AddWithValue("$pullRequest", task.PullRequest);
            command.Parameters.AddWithValue("$head", task.HeadSha);
            command.Parameters.AddWithValue("$approval", task.ApprovalId.ToString("D"));
            command.ExecuteNonQuery();
        }
    }

    public async ValueTask<ClaimResult> ClaimAsync(string executor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executor);
        // The stop is read first and again by whoever hands out: nothing leaves the queue while it is engaged
        if (await killSwitch.IsEngagedAsync(cancellationToken))
        {
            return new ClaimResult(ClaimStatus.Stopped);
        }
        try
        {
            return database.InWriteTransaction(connection => Claim(connection, executor));
        }
        catch (Exception failure) when (SqliteDatabase.IsUnavailable(failure))
        {
            return new ClaimResult(ClaimStatus.Unavailable);
        }
    }

    private (bool Commit, ClaimResult Result) Claim(SqliteConnection connection, string executor)
    {
        var now = time.GetUtcNow();
        var entries = new List<AuditEntry>();

        ExpireDue(connection, now, entries);

        var waiting = NextWaiting(connection);
        BatchDelivery? delivery = null;
        if (waiting is { } next)
        {
            delivery = new BatchDelivery(next.Batch, next.Code);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE batches SET state = 'Delivered', code = NULL, executor = $executor, delivered_at = $now WHERE id = $id;";
                command.Parameters.AddWithValue("$executor", executor);
                command.Parameters.AddWithValue("$now", SqliteDatabase.Format(now));
                command.Parameters.AddWithValue("$id", next.Batch.Id.ToString("D"));
                command.ExecuteNonQuery();
            }
            SqliteApprovalStore.SetStateOfBatch(connection, next.Batch.Id, ApprovalState.Taken, now);
            entries.Add(new AuditEntry(now, AuditEvents.BatchDelivered, executor, next.Batch.Id.ToString("D"), $"{next.Batch.Tasks.Count} tasks, digest {next.Batch.Digest()[..16]}"));
        }

        if (entries.Count > 0 && !AuditCapacityPolicy.Fits(auditCapacity, SqliteAuditLog.Count(connection), SqliteAuditLog.RefusalCount(connection), entries))
        {
            // Nothing is changed that the trail cannot say
            return (false, new ClaimResult(ClaimStatus.Unavailable));
        }
        foreach (var entry in entries)
        {
            SqliteAuditLog.Insert(connection, entry);
        }
        return (true, delivery is null ? new ClaimResult(ClaimStatus.Nothing) : new ClaimResult(ClaimStatus.Claimed, delivery));
    }

    // Past their time: a batch nobody took, and one that was handed out and never answered
    private void ExpireDue(SqliteConnection connection, DateTimeOffset now, List<AuditEntry> entries)
    {
        foreach (var id in DueBatches(connection, now))
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE batches SET state = 'Expired', code = NULL, ended_at = $now WHERE id = $id;";
                command.Parameters.AddWithValue("$now", SqliteDatabase.Format(now));
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                command.ExecuteNonQuery();
            }
            SqliteApprovalStore.SetStateOfBatch(connection, id, ApprovalState.Expired, now);
            entries.Add(new AuditEntry(now, AuditEvents.BatchExpired, AuditEvents.System, id.ToString("D"), null));
        }
    }

    public ValueTask<bool> ExpireDueAsync(CancellationToken cancellationToken)
    {
        try
        {
            return ValueTask.FromResult(database.InWriteTransaction(connection =>
            {
                var entries = new List<AuditEntry>();
                ExpireDue(connection, time.GetUtcNow(), entries);
                if (entries.Count > 0 && !AuditCapacityPolicy.Fits(auditCapacity, SqliteAuditLog.Count(connection), SqliteAuditLog.RefusalCount(connection), entries))
                {
                    // Nothing is changed that the trail cannot say
                    return (false, false);
                }
                foreach (var entry in entries)
                {
                    SqliteAuditLog.Insert(connection, entry);
                }
                return (true, true);
            }));
        }
        catch (Exception failure) when (SqliteDatabase.IsUnavailable(failure))
        {
            return ValueTask.FromResult(false);
        }
    }

    private List<Guid> DueBatches(SqliteConnection connection, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM batches WHERE (state = 'Waiting' AND expires_at <= $now) OR (state = 'Delivered' AND delivered_at <= $deliveryLimit);";
        command.Parameters.AddWithValue("$now", SqliteDatabase.Format(now));
        command.Parameters.AddWithValue("$deliveryLimit", SqliteDatabase.Format(now - options.DeliveryTimeout));
        using var reader = command.ExecuteReader();
        var due = new List<Guid>();
        while (reader.Read())
        {
            due.Add(Guid.Parse(reader.GetString(0)));
        }
        return due;
    }

    private static (TaskBatch Batch, string? Code)? NextWaiting(SqliteConnection connection)
    {
        string? id;
        string? code;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, code FROM batches WHERE state = 'Waiting' ORDER BY created_at, rowid LIMIT 1;";
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }
            id = reader.GetString(0);
            code = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        return (ReadBatch(connection, Guid.Parse(id)), code);
    }

    private static TaskBatch ReadBatch(SqliteConnection connection, Guid id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT work_item_id, approver, created_at, expires_at, delegation_id FROM batches WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        reader.Read();
        return new TaskBatch(
            id,
            reader.GetString(0),
            reader.GetString(1),
            SqliteDatabase.Parse(reader.GetString(2)),
            SqliteDatabase.Parse(reader.GetString(3)),
            ReadTasks(connection, id).Select(task => task.Spec).ToList(),
            reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)));
    }

    private static List<RunTask> ReadTasks(SqliteConnection connection, Guid batchId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, sequence, kind, repository, pull_request, head_sha, approval_id, result, reason FROM tasks WHERE batch_id = $batch ORDER BY sequence;";
        command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
        using var reader = command.ExecuteReader();
        var tasks = new List<RunTask>();
        while (reader.Read())
        {
            var spec = new TaskSpec(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), Enum.Parse<TaskKind>(reader.GetString(2)), reader.GetString(3), reader.GetInt32(4), reader.GetString(5), Guid.Parse(reader.GetString(6)));
            tasks.Add(new RunTask(spec, reader.IsDBNull(7) ? null : Enum.Parse<TaskResult>(reader.GetString(7)), reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return tasks;
    }

    public ValueTask<QueueResult> AppendEventsAsync(Guid batchId, string executor, IReadOnlyList<RunEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        try
        {
            return ValueTask.FromResult(database.InWriteTransaction(connection => AppendEvents(connection, batchId, executor, events)));
        }
        catch (Exception failure) when (SqliteDatabase.IsUnavailable(failure))
        {
            return ValueTask.FromResult(QueueResult.Unavailable);
        }
    }

    private (bool Commit, QueueResult Result) AppendEvents(SqliteConnection connection, Guid batchId, string executor, IReadOnlyList<RunEvent> events)
    {
        var state = StateOf(connection, batchId);
        if (state is null)
        {
            return (false, QueueResult.NotFound);
        }
        if (state.Value.State != BatchState.Delivered || state.Value.Executor != executor)
        {
            return (false, QueueResult.NotYours);
        }
        var (count, lastSequence, lastHash) = LastEvent(connection, batchId);
        var previous = lastHash ?? state.Value.Digest;
        if (count + events.Count > options.MaxEventsPerRun
            || events.Any(line => (line.Detail?.Length ?? 0) > options.MaxEventDetail || line.Type.Length > 64)
            || !RunEvent.Continues(previous, lastSequence, events))
        {
            return (false, QueueResult.Invalid);
        }
        foreach (var line in events)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO run_events (batch_id, sequence, at, type, task_id, detail, previous_hash, hash) VALUES ($batch, $sequence, $at, $type, $task, $detail, $previous, $hash);";
            command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
            command.Parameters.AddWithValue("$sequence", line.Sequence);
            command.Parameters.AddWithValue("$at", SqliteDatabase.Format(line.At));
            command.Parameters.AddWithValue("$type", line.Type);
            command.Parameters.AddWithValue("$task", line.TaskId is { } task ? task.ToString("D") : DBNull.Value);
            command.Parameters.AddWithValue("$detail", line.Detail is null ? DBNull.Value : line.Detail);
            command.Parameters.AddWithValue("$previous", line.PreviousHash);
            command.Parameters.AddWithValue("$hash", line.Hash);
            command.ExecuteNonQuery();
        }
        // A run that keeps sending lines is alive: the delivery timeout means "the executor went silent", not "the run is long"
        using var renew = connection.CreateCommand();
        renew.CommandText = "UPDATE batches SET delivered_at = $now WHERE id = $id AND state = 'Delivered';";
        renew.Parameters.AddWithValue("$now", SqliteDatabase.Format(time.GetUtcNow()));
        renew.Parameters.AddWithValue("$id", batchId.ToString("D"));
        renew.ExecuteNonQuery();
        return (true, QueueResult.Ok);
    }

    public ValueTask<QueueResult> CompleteAsync(Guid batchId, string executor, BatchOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        try
        {
            return ValueTask.FromResult(database.InWriteTransaction(connection => Complete(connection, batchId, executor, outcome)));
        }
        catch (Exception failure) when (SqliteDatabase.IsUnavailable(failure))
        {
            return ValueTask.FromResult(QueueResult.Unavailable);
        }
    }

    private (bool Commit, QueueResult Result) Complete(SqliteConnection connection, Guid batchId, string executor, BatchOutcome outcome)
    {
        var state = StateOf(connection, batchId);
        if (state is null)
        {
            return (false, QueueResult.NotFound);
        }
        if (state.Value.State != BatchState.Delivered || state.Value.Executor != executor)
        {
            return (false, QueueResult.NotYours);
        }
        var known = ReadTasks(connection, batchId).ToDictionary(task => task.Spec.Id, task => task.Spec.Kind);
        if (outcome.Final is not (BatchState.Done or BatchState.Refused) || outcome.Tasks.Any(task => !known.TryGetValue(task.TaskId, out var kind) || !TaskResultRules.Fits(kind, task.Result)))
        {
            return (false, QueueResult.Invalid);
        }

        var now = time.GetUtcNow();
        var entry = new AuditEntry(
            now,
            outcome.Final == BatchState.Done ? AuditEvents.BatchDone : AuditEvents.BatchRefused,
            executor,
            batchId.ToString("D"),
            outcome.Refusal is { } refusal ? refusal.ToString() : $"{outcome.Tasks.Count} tasks");
        if (!AuditCapacityPolicy.Fits(auditCapacity, SqliteAuditLog.Count(connection), SqliteAuditLog.RefusalCount(connection), [entry]))
        {
            return (false, QueueResult.Unavailable);
        }
        SqliteAuditLog.Insert(connection, entry);
        foreach (var task in outcome.Tasks)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE tasks SET result = $result, reason = $reason WHERE id = $id AND batch_id = $batch;";
            command.Parameters.AddWithValue("$result", task.Result.ToString());
            command.Parameters.AddWithValue("$reason", task.Reason is null ? DBNull.Value : task.Reason);
            command.Parameters.AddWithValue("$id", task.TaskId.ToString("D"));
            command.Parameters.AddWithValue("$batch", batchId.ToString("D"));
            command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE batches SET state = $state, refusal = $refusal, ended_at = $now WHERE id = $id;";
            command.Parameters.AddWithValue("$state", outcome.Final.ToString());
            command.Parameters.AddWithValue("$refusal", outcome.Refusal is { } code ? code.ToString() : DBNull.Value);
            command.Parameters.AddWithValue("$now", SqliteDatabase.Format(now));
            command.Parameters.AddWithValue("$id", batchId.ToString("D"));
            command.ExecuteNonQuery();
        }
        SqliteApprovalStore.SetStateOfBatch(connection, batchId, outcome.Final == BatchState.Done ? ApprovalState.DryRunDone : ApprovalState.Refused, now);
        return (true, QueueResult.Ok);
    }

    public IReadOnlyList<RunSummary> Runs(int limit) => SqliteDatabase.Guard<IReadOnlyList<RunSummary>>(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT b.id, b.work_item_id, b.approver, b.created_at, b.state, b.refusal, (SELECT COUNT(*) FROM tasks t WHERE t.batch_id = b.id), b.ended_at, b.executor FROM batches b ORDER BY b.created_at DESC, b.rowid DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var runs = new List<RunSummary>();
        while (reader.Read())
        {
            runs.Add(Summary(reader));
        }
        return runs;
    });

    public IReadOnlyList<TaskResultRow> TaskResults(int limit) => SqliteDatabase.Guard<IReadOnlyList<TaskResultRow>>(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT t.repository, t.pull_request, t.head_sha, t.kind, t.result, t.reason, b.ended_at FROM tasks t JOIN batches b ON b.id = t.batch_id WHERE t.result IS NOT NULL AND b.ended_at IS NOT NULL ORDER BY b.ended_at DESC, t.sequence DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var rows = new List<TaskResultRow>();
        while (reader.Read())
        {
            rows.Add(new TaskResultRow(
                reader.GetString(0), reader.GetInt32(1), reader.GetString(2), Enum.Parse<TaskKind>(reader.GetString(3)), Enum.Parse<TaskResult>(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5), SqliteDatabase.Parse(reader.GetString(6))));
        }
        return rows;
    });

    public RunDetail? Find(Guid batchId) => SqliteDatabase.Guard(() =>
    {
        using var connection = database.Connect();
        RunSummary? summary;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT b.id, b.work_item_id, b.approver, b.created_at, b.state, b.refusal, (SELECT COUNT(*) FROM tasks t WHERE t.batch_id = b.id), b.ended_at, b.executor FROM batches b WHERE b.id = $id;";
            command.Parameters.AddWithValue("$id", batchId.ToString("D"));
            using var reader = command.ExecuteReader();
            summary = reader.Read() ? Summary(reader) : null;
        }
        if (summary is null)
        {
            return null;
        }
        using var events = connection.CreateCommand();
        events.CommandText = "SELECT sequence, at, type, task_id, detail, previous_hash, hash FROM run_events WHERE batch_id = $id ORDER BY sequence;";
        events.Parameters.AddWithValue("$id", batchId.ToString("D"));
        using var lines = events.ExecuteReader();
        var trace = new List<RunEvent>();
        while (lines.Read())
        {
            trace.Add(new RunEvent(lines.GetInt64(0), SqliteDatabase.Parse(lines.GetString(1)), lines.GetString(2), lines.IsDBNull(3) ? null : Guid.Parse(lines.GetString(3)), lines.IsDBNull(4) ? null : lines.GetString(4), lines.GetString(5), lines.GetString(6)));
        }
        return new RunDetail(summary, ReadTasks(connection, batchId), trace);
    });

    private static RunSummary Summary(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        SqliteDatabase.Parse(reader.GetString(3)),
        Enum.Parse<BatchState>(reader.GetString(4)),
        reader.IsDBNull(5) ? null : Enum.Parse<RunRefusal>(reader.GetString(5)),
        reader.GetInt32(6),
        reader.IsDBNull(7) ? null : SqliteDatabase.Parse(reader.GetString(7)),
        reader.IsDBNull(8) ? null : reader.GetString(8));

    private static (BatchState State, string? Executor, string Digest)? StateOf(SqliteConnection connection, Guid batchId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state, executor, digest FROM batches WHERE id = $id;";
        command.Parameters.AddWithValue("$id", batchId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        string? executor = reader.IsDBNull(1) ? null : reader.GetString(1);
        return (Enum.Parse<BatchState>(reader.GetString(0)), executor, reader.GetString(2));
    }

    private static (int Count, long LastSequence, string? LastHash) LastEvent(SqliteConnection connection, Guid batchId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM run_events WHERE batch_id = $id), sequence, hash FROM run_events WHERE batch_id = $id ORDER BY sequence DESC LIMIT 1;";
        command.Parameters.AddWithValue("$id", batchId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture), reader.GetInt64(1), reader.GetString(2)) : (0, 0L, null);
    }
}
