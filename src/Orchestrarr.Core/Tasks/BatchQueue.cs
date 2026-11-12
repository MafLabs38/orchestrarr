namespace Orchestrarr.Core.Tasks;

public enum ClaimStatus
{
    /// <summary>Nothing waits for this executor.</summary>
    Nothing,

    /// <summary>The global stop is engaged: nothing is handed out, and the executor must not start anything.</summary>
    Stopped,

    Claimed,

    /// <summary>The queue or the trail cannot be used: nothing was handed out.</summary>
    Unavailable,
}

public sealed record ClaimResult(ClaimStatus Status, BatchDelivery? Delivery = null);

public enum QueueResult
{
    Ok,
    NotFound,

    /// <summary>The batch is not in the state this needs, or it is with another executor.</summary>
    NotYours,

    /// <summary>The lines do not continue the chain of the run, one is altered, or there are too many or too long.</summary>
    Invalid,
    Unavailable,
}

/// <summary>A run in a list: one batch and where it stands.</summary>
public sealed record RunSummary(
    Guid BatchId,
    string WorkItemId,
    string ApproverSubject,
    DateTimeOffset CreatedAt,
    BatchState State,
    RunRefusal? Refusal,
    int TaskCount,
    DateTimeOffset? EndedAt,
    string? Executor);

public sealed record RunTask(TaskSpec Spec, TaskResult? Result, string? Reason);

/// <summary>What became of one task of an ended batch, on one commit of one pull request.</summary>
public sealed record TaskResultRow(string Repository, int PullRequest, string HeadSha, TaskKind Kind, TaskResult Result, string? Reason, DateTimeOffset At);

/// <summary>A run with everything it left: its tasks, what became of each, and the chained lines of its trace.</summary>
public sealed record RunDetail(RunSummary Summary, IReadOnlyList<RunTask> Tasks, IReadOnlyList<RunEvent> Events);

/// <summary>
/// The side of the queue that the executor uses, and the side that shows the runs. The executor only connects out: it asks for a batch,
/// sends the lines of its trace and says how it ended. A batch is handed to one executor once (ADR 0017). Writing a batch in is not here: it
/// goes through <see cref="Approvals.IApprovalLedger"/> only.
/// </summary>
public interface IBatchQueue
{
    /// <summary>
    /// Hands the oldest waiting batch to this executor, once, and takes the code out of the queue. A batch that waited past its time ends as
    /// expired first, and so does one that was handed out and never answered. While the global stop is engaged nothing is handed out.
    /// </summary>
    ValueTask<ClaimResult> ClaimAsync(string executor, CancellationToken cancellationToken);

    /// <summary>
    /// Ends as expired the batches that waited past their time and the ones that were handed out and never answered, and frees their
    /// approvals. It does not depend on an executor asking for work, and it hands nothing out, so the global stop does not hold it back.
    /// False when the store or the trail could not take it (nothing was changed).
    /// </summary>
    ValueTask<bool> ExpireDueAsync(CancellationToken cancellationToken);

    ValueTask<QueueResult> AppendEventsAsync(Guid batchId, string executor, IReadOnlyList<RunEvent> events, CancellationToken cancellationToken);

    ValueTask<QueueResult> CompleteAsync(Guid batchId, string executor, BatchOutcome outcome, CancellationToken cancellationToken);

    /// <summary>The newest runs first: a list in a file can be long.</summary>
    IReadOnlyList<RunSummary> Runs(int limit);

    RunDetail? Find(Guid batchId);

    /// <summary>What became of the tasks that ended, newest first: the workshop's latest word on a pull request, which the table reads.</summary>
    IReadOnlyList<TaskResultRow> TaskResults(int limit);
}

public sealed record TaskOptions
{
    /// <summary>How long an approval waits for the executor before it expires and has to be given again.</summary>
    public TimeSpan BatchLifetime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How long a batch may stay with an executor that does not answer, before it ends as expired.</summary>
    public TimeSpan DeliveryTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Whether an approval needs the code of the second factor (the executor checks it). Off only for a sandbox with no executor.</summary>
    public bool RequireSecondFactor { get; init; } = true;

    /// <summary>The most lines a run may hold, and the longest line: an executor that was taken over cannot fill the store.</summary>
    public int MaxEventsPerRun { get; init; } = TaskLimits.MaxEventsPerRun;

    public int MaxEventDetail { get; init; } = TaskLimits.MaxEventDetail;

    /// <summary>How often the host ends the batches that are past their time, whether or not an executor asks for work.</summary>
    public TimeSpan ExpirySweepInterval { get; init; } = TimeSpan.FromSeconds(15);
}
