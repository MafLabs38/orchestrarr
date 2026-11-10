using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Tasks;

/// <summary>
/// The queue of the sandbox: batches in memory, with the run lines. A restart forgets it. It hands a batch out once, ends the ones that
/// waited too long or were never answered, and keeps the approvals in step with the batch they belong to.
/// </summary>
public sealed class InMemoryBatchQueue(InMemoryApprovalStore approvals, IAuditSink audit, IKillSwitch killSwitch, TimeProvider time, TaskOptions options) : IBatchQueue
{
    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];

    /// <summary>Adds a batch to the queue. Only the in-memory ledger calls this, after it checked the stop and the room.</summary>
    internal void Add(TaskBatch batch, string? code)
    {
        lock (_gate)
        {
            _entries.Add(new Entry(batch, code));
        }
    }

    public async ValueTask<ClaimResult> ClaimAsync(string executor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executor);
        if (await killSwitch.IsEngagedAsync(cancellationToken))
        {
            return new ClaimResult(ClaimStatus.Stopped);
        }
        var now = time.GetUtcNow();
        List<Entry> expired;
        lock (_gate)
        {
            expired = [.. _entries.Where(entry => IsDue(entry, now))];
        }

        foreach (var entry in expired)
        {
            if (!await audit.TryRecordAsync(new AuditEntry(now, AuditEvents.BatchExpired, AuditEvents.System, entry.Batch.Id.ToString("D"), $"{entry.State}"), cancellationToken))
            {
                return new ClaimResult(ClaimStatus.Unavailable);
            }
            lock (_gate)
            {
                entry.State = BatchState.Expired;
                entry.Code = null;
                entry.EndedAt = now;
            }
            approvals.SetState(entry.Batch.Id, ApprovalState.Expired, now);
        }

        // The batch is reserved in the same step that picks it, so that two claims at once never hand out the same one; the code leaves the
        // queue with the batch (handed out once)
        Entry? claimed;
        string? code;
        lock (_gate)
        {
            claimed = _entries.Where(entry => entry.State == BatchState.Waiting).OrderBy(entry => entry.Batch.CreatedAt).FirstOrDefault();
            code = claimed?.Code;
            if (claimed is not null)
            {
                claimed.Code = null;
                claimed.State = BatchState.Delivered;
                claimed.Executor = executor;
                claimed.DeliveredAt = now;
            }
        }
        if (claimed is null)
        {
            return new ClaimResult(ClaimStatus.Nothing);
        }
        if (!await audit.TryRecordAsync(new AuditEntry(now, AuditEvents.BatchDelivered, executor, claimed.Batch.Id.ToString("D"), $"{claimed.Batch.Tasks.Count} tasks, digest {claimed.Batch.Digest()[..16]}"), cancellationToken))
        {
            // Not delivered after all: it waits again, with its code
            lock (_gate)
            {
                claimed.Code = code;
                claimed.State = BatchState.Waiting;
                claimed.Executor = null;
                claimed.DeliveredAt = null;
            }
            return new ClaimResult(ClaimStatus.Unavailable);
        }
        approvals.SetState(claimed.Batch.Id, ApprovalState.Taken, now);
        return new ClaimResult(ClaimStatus.Claimed, new BatchDelivery(claimed.Batch, code));
    }

    public ValueTask<QueueResult> AppendEventsAsync(Guid batchId, string executor, IReadOnlyList<RunEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        lock (_gate)
        {
            var entry = _entries.Find(candidate => candidate.Batch.Id == batchId);
            if (entry is null)
            {
                return ValueTask.FromResult(QueueResult.NotFound);
            }
            if (entry.State != BatchState.Delivered || entry.Executor != executor)
            {
                return ValueTask.FromResult(QueueResult.NotYours);
            }
            var previous = entry.Events.Count == 0 ? entry.Batch.Digest() : entry.Events[^1].Hash;
            var last = entry.Events.Count == 0 ? 0 : entry.Events[^1].Sequence;
            if (entry.Events.Count + events.Count > options.MaxEventsPerRun
                || events.Any(line => (line.Detail?.Length ?? 0) > options.MaxEventDetail || line.Type.Length > 64)
                || !RunEvent.Continues(previous, last, events))
            {
                return ValueTask.FromResult(QueueResult.Invalid);
            }
            entry.Events.AddRange(events);
            return ValueTask.FromResult(QueueResult.Ok);
        }
    }

    public async ValueTask<QueueResult> CompleteAsync(Guid batchId, string executor, BatchOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        Entry? entry;
        lock (_gate)
        {
            entry = _entries.Find(candidate => candidate.Batch.Id == batchId);
        }
        if (entry is null)
        {
            return QueueResult.NotFound;
        }
        var known = entry.Batch.Tasks.Select(task => task.Id).ToHashSet();
        if (entry.State != BatchState.Delivered || entry.Executor != executor)
        {
            return QueueResult.NotYours;
        }
        if (outcome.Final is not (BatchState.Done or BatchState.Refused) || outcome.Tasks.Any(task => !known.Contains(task.TaskId)))
        {
            return QueueResult.Invalid;
        }

        var now = time.GetUtcNow();
        var name = outcome.Final == BatchState.Done ? AuditEvents.BatchDone : AuditEvents.BatchRefused;
        var detail = outcome.Refusal is { } refusal ? refusal.ToString() : $"{outcome.Tasks.Count} tasks";
        if (!await audit.TryRecordAsync(new AuditEntry(now, name, executor, batchId.ToString("D"), detail), cancellationToken))
        {
            return QueueResult.Unavailable;
        }
        lock (_gate)
        {
            // Another report of the same batch may have ended it while the trace was written: the first one stands
            if (entry.State != BatchState.Delivered || entry.Executor != executor)
            {
                return QueueResult.NotYours;
            }
            entry.State = outcome.Final;
            entry.Refusal = outcome.Refusal;
            entry.Outcomes = outcome.Tasks.ToDictionary(task => task.TaskId);
            entry.EndedAt = now;
        }
        approvals.SetState(batchId, outcome.Final == BatchState.Done ? ApprovalState.DryRunDone : ApprovalState.Refused, now);
        return QueueResult.Ok;
    }

    public IReadOnlyList<RunSummary> Runs(int limit)
    {
        lock (_gate)
        {
            return [.. _entries.OrderByDescending(entry => entry.Batch.CreatedAt).Take(limit).Select(Summary)];
        }
    }

    public RunDetail? Find(Guid batchId)
    {
        lock (_gate)
        {
            var entry = _entries.Find(candidate => candidate.Batch.Id == batchId);
            return entry is null
                ? null
                : new RunDetail(
                    Summary(entry),
                    [.. entry.Batch.Tasks.OrderBy(task => task.Sequence).Select(task => entry.Outcomes.TryGetValue(task.Id, out var outcome) ? new RunTask(task, outcome.Result, outcome.Reason) : new RunTask(task, null, null))],
                    [.. entry.Events]);
        }
    }

    private bool IsDue(Entry entry, DateTimeOffset now) =>
        (entry.State == BatchState.Waiting && entry.Batch.ExpiresAt <= now)
        || (entry.State == BatchState.Delivered && entry.DeliveredAt + options.DeliveryTimeout <= now);

    private static RunSummary Summary(Entry entry) =>
        new(entry.Batch.Id, entry.Batch.WorkItemId, entry.Batch.ApproverSubject, entry.Batch.CreatedAt, entry.State, entry.Refusal, entry.Batch.Tasks.Count, entry.EndedAt, entry.Executor);

    private sealed class Entry(TaskBatch batch, string? code)
    {
        public TaskBatch Batch { get; } = batch;

        public string? Code { get; set; } = code;

        public BatchState State { get; set; } = BatchState.Waiting;

        public string? Executor { get; set; }

        public DateTimeOffset? DeliveredAt { get; set; }

        public DateTimeOffset? EndedAt { get; set; }

        public RunRefusal? Refusal { get; set; }

        public Dictionary<Guid, TaskOutcome> Outcomes { get; set; } = [];

        public List<RunEvent> Events { get; } = [];
    }
}
