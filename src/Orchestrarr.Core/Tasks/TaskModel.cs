using System.Security.Cryptography;

namespace Orchestrarr.Core.Tasks;

/// <summary>
/// The closed catalogue of what a task can be (ADR 0017). There is no free command and no free prompt: a kind is a name with parameters
/// that are checked by shape, and what the agent is told is built from the kind and from templates, never from outside text.
/// </summary>
public enum TaskKind
{
    ValidatePullRequest,
    MergePullRequest,
}

/// <summary>Where a batch is: waiting for the executor, with it, or ended. A batch that ends never starts again; a new approval makes a new batch.</summary>
public enum BatchState
{
    Waiting,
    Delivered,

    /// <summary>Every task ran to the end of what this slice does: with the dry run, "would have started".</summary>
    Done,

    /// <summary>The executor refused the batch, with a code.</summary>
    Refused,

    /// <summary>Nobody took it before it expired.</summary>
    Expired,
}

/// <summary>What happened to one task. <c>DryRun</c>: it was checked in every way and written down, nothing was started.</summary>
public enum TaskResult
{
    DryRun,
    Refused,
    Skipped,

    /// <summary>The task ran and every check passed (a validation: the build and the tests are green).</summary>
    Passed,

    /// <summary>The task ran and a check did not pass. It is a result, not an error: what follows for the same approval does not run.</summary>
    Failed,
}

/// <summary>Why the executor refused a batch or a task. A code, never a sentence (an attacker learns nothing from the difference).</summary>
public enum RunRefusal
{
    Stopped,
    Expired,
    Replayed,
    SecondFactor,
    DelegationInvalid,
    KindNotAllowed,
    RepositoryNotAllowed,
    JournalMismatch,
    Malformed,
}

/// <summary>One thing to do, in the order of its batch. <c>ApprovalId</c> ties it to the decision of the operator that it carries out.</summary>
public sealed record TaskSpec(Guid Id, int Sequence, TaskKind Kind, string Repository, int PullRequest, string HeadSha, Guid ApprovalId);

/// <summary>Bounds that the control plane and the executor both apply, so that an approval is never recorded for a batch the executor would always refuse.</summary>
public static class TaskLimits
{
    /// <summary>The most tasks one batch may hold (two per pull request when validating and merging).</summary>
    public const int MaxTasksPerBatch = 200;

    /// <summary>
    /// The most characters of the detail of one trace line. A line that carries the end of a command's output needs room; a longer one is cut by the
    /// executor before it is sent (a trace the control plane refuses as a whole is worse than a cut line).
    /// </summary>
    public const int MaxEventDetail = 4000;
}

/// <summary>
/// What one click of approval produces: the tasks it covers, who approved, until when it is good. <c>DelegationId</c> is set when a standing
/// delegation stands in for the code (ADR 0017); the executor checks the delegation in its own store.
/// </summary>
public sealed record TaskBatch(
    Guid Id,
    string WorkItemId,
    string ApproverSubject,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<TaskSpec> Tasks,
    Guid? DelegationId = null)
{
    /// <summary>Changing the encoding below changes this tag, so a batch of one form never matches the digest of another.</summary>
    public const string FormatTag = "orchestrarr-batch-v1";

    /// <summary>
    /// The exact bytes the digest covers: every field with a presence byte and a length, the tasks in their order, the times in whole
    /// seconds. Two batches that differ in anything an executor acts on differ here.
    /// </summary>
    public byte[] Canonical()
    {
        var writer = new CanonicalWriter();
        writer.Text(FormatTag);
        writer.Text(Id.ToString("D"));
        writer.Text(WorkItemId);
        writer.Text(ApproverSubject);
        writer.Seconds(CreatedAt);
        writer.Seconds(ExpiresAt);
        writer.Text(DelegationId?.ToString("D"));
        var tasks = Tasks.OrderBy(task => task.Sequence).ToArray();
        writer.Int(tasks.Length);
        foreach (var task in tasks)
        {
            writer.Text(task.Id.ToString("D"));
            writer.Int(task.Sequence);
            writer.Text(task.Kind.ToString());
            writer.Text(task.Repository);
            writer.Int(task.PullRequest);
            writer.Text(task.HeadSha);
            writer.Text(task.ApprovalId.ToString("D"));
        }
        return writer.ToArray();
    }

    /// <summary>The identifier of the content, in the journal and as the start of the chain of the events of a run.</summary>
    public string Digest() => Convert.ToHexString(SHA256.HashData(Canonical())).ToLowerInvariant();
}

/// <summary>A batch as it is handed to the executor, with the code of the second factor if there is one. The code is handed over once and not kept.</summary>
public sealed record BatchDelivery(TaskBatch Batch, string? SecondFactorCode);

public sealed record TaskOutcome(Guid TaskId, TaskResult Result, string? Reason);

/// <summary>How the executor ends a batch: <c>Final</c> is <see cref="BatchState.Done"/> or <see cref="BatchState.Refused"/>.</summary>
public sealed record BatchOutcome(BatchState Final, RunRefusal? Refusal, IReadOnlyList<TaskOutcome> Tasks);

/// <summary>
/// One line of the trace of a run. The lines are chained: each carries the hash of the one before, the first the digest of the batch, so
/// that a line removed or changed afterwards shows. The executor writes them; the control plane checks the chain and keeps them.
/// </summary>
public sealed record RunEvent(long Sequence, DateTimeOffset At, string Type, Guid? TaskId, string? Detail, string PreviousHash, string Hash)
{
    public static RunEvent Create(string previousHash, long sequence, DateTimeOffset at, string type, Guid? taskId, string? detail)
    {
        var moment = DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds());
        return new RunEvent(sequence, moment, type, taskId, detail, previousHash, Compute(previousHash, sequence, moment, type, taskId, detail));
    }

    public static string Compute(string previousHash, long sequence, DateTimeOffset at, string type, Guid? taskId, string? detail)
    {
        var writer = new CanonicalWriter();
        writer.Text(previousHash);
        writer.Long(sequence);
        writer.Seconds(at);
        writer.Text(type);
        writer.Text(taskId?.ToString("D"));
        writer.Text(detail);
        return Convert.ToHexString(SHA256.HashData(writer.ToArray())).ToLowerInvariant();
    }

    /// <summary>Whether these lines continue a chain that stood at <paramref name="previousHash"/> and <paramref name="lastSequence"/>, each intact.</summary>
    public static bool Continues(string previousHash, long lastSequence, IReadOnlyList<RunEvent> events)
    {
        var hash = previousHash;
        var sequence = lastSequence;
        foreach (var line in events)
        {
            if (line.Sequence != sequence + 1 || line.PreviousHash != hash || line.Hash != Compute(hash, line.Sequence, line.At, line.Type, line.TaskId, line.Detail))
            {
                return false;
            }
            hash = line.Hash;
            sequence = line.Sequence;
        }
        return true;
    }
}

public static class RunEvents
{
    public const string Claimed = "run.claimed";
    public const string Stopped = "run.stopped";
    public const string BatchChecked = "run.batch-checked";
    public const string SecondFactorChecked = "run.second-factor";
    public const string DelegationChecked = "run.delegation";
    public const string RightsChecked = "run.rights";
    public const string DoubleControl = "run.double-control";
    public const string TaskDryRun = "task.dry-run";
    public const string TaskStarted = "task.started";
    public const string TaskStep = "task.step";
    public const string TaskPassed = "task.passed";
    public const string TaskFailed = "task.failed";
    public const string TaskRefused = "task.refused";
    public const string TaskSkipped = "task.skipped";
    public const string RunCompleted = "run.completed";
    public const string RunRefused = "run.refused";
}
