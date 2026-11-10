using Orchestrarr.Core.SecondFactor;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor;

/// <summary>What the executor did with a batch: how it ended, and the chained lines of its trace.</summary>
public sealed record PipelineResult(BatchOutcome Outcome, IReadOnlyList<RunEvent> Events);

/// <summary>What carries out one task. A kind with no runner is refused. The first runner only says what it would have done (the dry run).</summary>
public interface ITaskRunner
{
    TaskKind Kind { get; }

    ValueTask<TaskOutcome> RunAsync(TaskSpec task, RunTrace trace, CancellationToken cancellationToken);
}

/// <summary>The trace of one run: each line is chained to the one before (the first to the digest of the batch) and kept in the executor's own file first.</summary>
public sealed class RunTrace
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly Guid _batchId;
    private readonly TimeProvider _time;
    private readonly ExecutorStore _store;
    private readonly List<RunEvent> _events = [];
    private string _previous;

    internal RunTrace(Guid batchId, string digest, TimeProvider time, ExecutorStore store)
    {
        _batchId = batchId;
        _previous = digest;
        _time = time;
        _store = store;
    }

    public IReadOnlyList<RunEvent> Events => _events;

    public void Add(string type, Guid? taskId = null, string? detail = null)
    {
        var line = RunEvent.Create(_previous, _events.Count + 1L, _time.GetUtcNow(), type, taskId, detail);
        _store.AppendTrace(_runId, _batchId, line);
        _events.Add(line);
        _previous = line.Hash;
    }
}

/// <summary>
/// What the executor checks, in this order, before it does anything (ADR 0017): the stop, the shape of the batch, its time, the double control
/// against the journal, its own rights, that it was never run, and then the authorization: the code of the second factor, or a standing
/// delegation that it holds itself. Any failure refuses the whole batch, with a code, and the reason goes in the trace. Nothing in a refusal
/// says more than the code (an attacker learns nothing from the difference) except in the trace, which only operators read.
/// </summary>
public sealed class ExecutorPipeline(
    ExecutorStore store,
    SecondFactorGuard secondFactor,
    IApprovalJournalReader journal,
    RightsOptions rights,
    IEnumerable<ITaskRunner> runners,
    TimeProvider time,
    string executorName)
{
    private readonly Dictionary<TaskKind, ITaskRunner> _runners = runners.ToDictionary(runner => runner.Kind);

    // isStopped is read before the batch is accepted and again before each task: a stop engaged during a run ends it before the next task
    public async Task<PipelineResult> RunAsync(BatchDelivery delivery, Func<bool> isStopped, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(isStopped);
        var batch = delivery.Batch;
        var trace = new RunTrace(batch.Id, batch.Digest(), time, store);
        trace.Add(RunEvents.Claimed, null, $"executor {executorName}, {batch.Tasks.Count} tasks");

        var refusal = await FirstRefusalAsync(delivery, isStopped(), trace, cancellationToken);
        if (refusal is not null)
        {
            trace.Add(RunEvents.RunRefused, null, refusal.Value.ToString());
            return new PipelineResult(new BatchOutcome(BatchState.Refused, refusal, []), trace.Events);
        }

        var outcomes = new List<TaskOutcome>();
        // A task that is refused or fails stops what follows for the same approval: a merge never runs after its validation did not pass
        var stoppedApprovals = new HashSet<Guid>();
        var stopNoted = false;
        foreach (var task in batch.Tasks.OrderBy(task => task.Sequence))
        {
            if (isStopped())
            {
                if (!stopNoted)
                {
                    trace.Add(RunEvents.Stopped, null, "the local stop was engaged during the run");
                    stopNoted = true;
                }
                trace.Add(RunEvents.TaskSkipped, task.Id, "the stop was engaged");
                outcomes.Add(new TaskOutcome(task.Id, TaskResult.Skipped, "stopped"));
                continue;
            }
            if (stoppedApprovals.Contains(task.ApprovalId))
            {
                trace.Add(RunEvents.TaskSkipped, task.Id, "an earlier task of this approval did not pass");
                outcomes.Add(new TaskOutcome(task.Id, TaskResult.Skipped, "earlier task did not pass"));
                continue;
            }
            if (!_runners.TryGetValue(task.Kind, out var runner))
            {
                trace.Add(RunEvents.TaskRefused, task.Id, "no runner for this kind");
                outcomes.Add(new TaskOutcome(task.Id, TaskResult.Refused, "no runner"));
                stoppedApprovals.Add(task.ApprovalId);
                continue;
            }
            try
            {
                var outcome = await runner.RunAsync(task, trace, cancellationToken);
                outcomes.Add(outcome);
                if (outcome.Result == TaskResult.Refused)
                {
                    stoppedApprovals.Add(task.ApprovalId);
                }
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                // A runner that fails ends its task as refused, with a trace, and the batch still ends: it must not stay delivered with no end
                // The kind of failure only, never its message: a message can carry what a command printed
                trace.Add(RunEvents.TaskRefused, task.Id, $"the runner failed ({failure.GetType().Name})");
                outcomes.Add(new TaskOutcome(task.Id, TaskResult.Refused, "runner failed"));
                stoppedApprovals.Add(task.ApprovalId);
            }
        }
        trace.Add(RunEvents.RunCompleted, null, $"{outcomes.Count} tasks");
        return new PipelineResult(new BatchOutcome(BatchState.Done, null, outcomes), trace.Events);
    }

    private async Task<RunRefusal?> FirstRefusalAsync(BatchDelivery delivery, bool stopped, RunTrace trace, CancellationToken cancellationToken)
    {
        var batch = delivery.Batch;
        if (stopped)
        {
            trace.Add(RunEvents.Stopped, null, "the global stop is engaged");
            return RunRefusal.Stopped;
        }
        if (!TaskShape.IsWellFormed(batch))
        {
            trace.Add(RunEvents.BatchChecked, null, "the batch is not well formed");
            return RunRefusal.Malformed;
        }
        if (time.GetUtcNow() >= batch.ExpiresAt)
        {
            trace.Add(RunEvents.BatchChecked, null, $"expired at {batch.ExpiresAt:O}");
            return RunRefusal.Expired;
        }
        trace.Add(RunEvents.BatchChecked, null, "well formed, not expired");

        // The double control: the batch must be the one that was approved, in a record that a line written into the queue alone does not reach
        var check = await journal.CheckAsync(batch.Id, batch.Digest(), cancellationToken);
        if (check != JournalCheck.Found)
        {
            trace.Add(RunEvents.DoubleControl, null, $"journal: {check}");
            return RunRefusal.JournalMismatch;
        }
        trace.Add(RunEvents.DoubleControl, null, "the journal holds this batch with this digest");

        if (RightsRefusal(batch) is { } rightsRefusal)
        {
            trace.Add(RunEvents.RightsChecked, null, rightsRefusal.Detail);
            return rightsRefusal.Refusal;
        }
        trace.Add(RunEvents.RightsChecked, null, "every kind and repository is within this executor's rights");

        // Written down before the authorization: a batch is never run twice, whatever happens next
        if (!store.TryRegisterBatch(batch.Id, time.GetUtcNow()))
        {
            trace.Add(RunEvents.BatchChecked, null, "this batch was already seen by this executor");
            return RunRefusal.Replayed;
        }

        return batch.DelegationId is null
            ? await CheckSecondFactorAsync(delivery, trace, cancellationToken)
            : CheckDelegation(batch, trace);
    }

    private (RunRefusal Refusal, string Detail)? RightsRefusal(TaskBatch batch)
    {
        foreach (var task in batch.Tasks)
        {
            if (!rights.Kinds.Contains(task.Kind))
            {
                return (RunRefusal.KindNotAllowed, $"{task.Kind} is not among the kinds this executor accepts");
            }
            if (!rights.Repositories.Any(pattern => TaskShape.Matches(pattern, task.Repository)))
            {
                return (RunRefusal.RepositoryNotAllowed, $"{task.Repository} is not among the repositories this executor accepts");
            }
        }
        return null;
    }

    private async Task<RunRefusal?> CheckSecondFactorAsync(BatchDelivery delivery, RunTrace trace, CancellationToken cancellationToken)
    {
        var result = await secondFactor.VerifyAsync(delivery.Batch.ApproverSubject, delivery.SecondFactorCode, cancellationToken);
        if (result != SecondFactorResult.Verified)
        {
            trace.Add(RunEvents.SecondFactorChecked, null, $"refused: {result}");
            return RunRefusal.SecondFactor;
        }
        trace.Add(RunEvents.SecondFactorChecked, null, $"code of {delivery.Batch.ApproverSubject} verified");
        return null;
    }

    private RunRefusal? CheckDelegation(TaskBatch batch, RunTrace trace)
    {
        var now = time.GetUtcNow();
        var delegation = batch.DelegationId is { } id ? store.FindDelegation(id) : null;
        if (delegation is null
            || delegation.RevokedAt is not null
            || delegation.ExpiresAt <= now
            || batch.ApproverSubject != $"delegation:{delegation.Id:D}"
            || batch.Tasks.Any(task => task.Kind != delegation.Kind || !TaskShape.Matches(delegation.Repository, task.Repository)))
        {
            trace.Add(RunEvents.DelegationChecked, null, "refused: no delegation in force covers this batch");
            return RunRefusal.DelegationInvalid;
        }
        if (!store.TryUseDelegation(delegation.Id, delegation.DailyQuota, batch.Tasks.Count, now))
        {
            trace.Add(RunEvents.DelegationChecked, null, "refused: the daily quota of the delegation is used");
            return RunRefusal.DelegationInvalid;
        }
        trace.Add(RunEvents.DelegationChecked, null, $"delegation {delegation.Id:D} of {delegation.Subject} covers this batch");
        return null;
    }
}

/// <summary>
/// The first runner: it checks nothing more and starts nothing, it writes what it would have done (ADR 0017: the first slice is a dry run).
/// A real runner replaces it with the vertical that acts.
/// </summary>
public sealed class DryRunTaskRunner(TaskKind kind) : ITaskRunner
{
    public TaskKind Kind { get; } = kind;

    public ValueTask<TaskOutcome> RunAsync(TaskSpec task, RunTrace trace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(trace);
        var what = Describe(task);
        trace.Add(RunEvents.TaskDryRun, task.Id, what);
        return ValueTask.FromResult(new TaskOutcome(task.Id, TaskResult.DryRun, what));
    }

    // The shape check guarantees a full-length commit, but a description must never be the thing that fails on a short one
    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    private static string Describe(TaskSpec task) => task.Kind switch
    {
        TaskKind.ValidatePullRequest => $"would validate {task.Repository}#{task.PullRequest} at {Short(task.HeadSha)}: a workshop run with the build, the tests and a regression check",
        TaskKind.MergePullRequest => $"would merge {task.Repository}#{task.PullRequest} at {Short(task.HeadSha)}, only once validated and with the forge's checks green on this commit",
        _ => $"would run {task.Kind}",
    };
}
