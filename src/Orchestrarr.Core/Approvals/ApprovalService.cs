using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Approvals;

/// <summary>
/// Records the operator's approval of a decision card (SPEC F-CRD-01, D-37, D-52). The server rebuilds everything from the current work
/// item; what the client sends is the work item, the steps it ticked and the pull requests (number and commit) it displayed, which must be
/// exactly the ones there now: an approval covers what the operator saw, never a commit that arrived after. Nothing here runs anything; an
/// approval makes a batch of typed tasks that waits for the executor, which checks the second factor itself (ADR 0017). This service is the
/// one entry of the queue. It fails closed: the kill switch, an unreadable or changed pull request list, an unrecordable trace each
/// refuse. A draft pull request is never approved.
/// </summary>
public sealed class ApprovalService(
    WorkItemCatalog catalog,
    IApprovalStore store,
    IKillSwitch killSwitch,
    IAuditSink audit,
    TimeProvider time,
    IApprovalLedger ledger,
    IApprovalJournal journal,
    TaskOptions taskOptions,
    ISecondFactorPolicy? secondFactor = null) : IDisposable
{
    // What this vertical records. The other steps are shown and can be granted, but run with later verticals.
    private static readonly StepKind[] Supported = [StepKind.Validate, StepKind.MergePullRequests];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RefusalTraceThrottle _refusals = new(time);

    public async Task<ApprovalResult> ApproveAsync(
        Approver approver,
        string workItemId,
        IReadOnlyList<StepKind> ticked,
        IReadOnlyList<SeenPullRequest> seen,
        string? secondFactorCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(ticked);
        ArgumentNullException.ThrowIfNull(seen);

        // Everything that reads or checks comes first, without the gate: a slow read of the forge must not block the other approvals.
        var (candidates, batchFor, refusal) = await CandidatesAsync(approver, workItemId, ticked, seen, secondFactorCode, cancellationToken);
        var result = refusal is null
            ? await RecordAsync(candidates, batchFor!, secondFactorCode, null, cancellationToken)
            : ApprovalResult.Refused(refusal.Value);
        if (!result.Recorded)
        {
            await RecordRefusalAsync(approver, workItemId, result.Refusal!.Value);
        }
        return result;
    }

    /// <summary>
    /// Approves a selection of problems with one code (ADR 0019): the dependency updates of several applications, each with the steps its row stands for,
    /// become <b>one batch</b> that the executor checks once. Each pull request is bound to the commit the operator saw, as with a card.
    /// </summary>
    public async Task<ApprovalResult> ApproveSelectionAsync(Approver approver, IReadOnlyList<SelectedProblem> selection, string? secondFactorCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(selection);

        var (candidates, batchFor, notes, refusal) = await SelectionCandidatesAsync(approver, selection, secondFactorCode, cancellationToken);
        var result = refusal is null
            ? await RecordAsync(candidates, batchFor!, secondFactorCode, notes, cancellationToken)
            : ApprovalResult.Refused(refusal.Value);
        if (!result.Recorded)
        {
            await RecordRefusalAsync(approver, selection.Count > 0 ? selection[0].WorkItemId : string.Empty, result.Refusal!.Value);
        }
        return result;
    }

    private async Task<(IReadOnlyList<Approval> Candidates, Func<IReadOnlyList<Approval>, TaskBatch>? BatchFor, IReadOnlyDictionary<Guid, string>? Notes, ApprovalRefusal? Refusal)> SelectionCandidatesAsync(
        Approver approver, IReadOnlyList<SelectedProblem> selection, string? secondFactorCode, CancellationToken cancellationToken)
    {
        if (await FrontRefusalAsync(approver, secondFactorCode, cancellationToken) is { } front)
        {
            return ([], null, null, front);
        }
        if (selection.Count == 0)
        {
            return ([], null, null, ApprovalRefusal.NoPullRequests);
        }
        // The same pull request twice is not a selection that can be approved
        if (selection.Select(problem => (problem.WorkItemId, problem.PullRequest)).Distinct().Count() != selection.Count)
        {
            return ([], null, null, ApprovalRefusal.PlanChanged);
        }

        var now = time.GetUtcNow();
        var batchId = Guid.NewGuid();
        var approvals = new List<Approval>();
        var notes = new Dictionary<Guid, string>();
        foreach (var group in selection.GroupBy(problem => problem.WorkItemId, StringComparer.Ordinal))
        {
            var item = await catalog.FindAsync(group.Key, cancellationToken);
            if (item?.RepositoryFullName is null)
            {
                return ([], null, null, ApprovalRefusal.UnknownWorkItem);
            }
            // What is approved must be what the operator sees now: a list that is still being read, failed, or only an old read is refused
            if (item.PullRequests is not { Status: PullRequestsStatus.Read, Stale: false })
            {
                return ([], null, null, ApprovalRefusal.PullRequestsNotRead);
            }
            foreach (var problem in group)
            {
                if (CheckSteps(item, problem.Steps) is { } stepsRefusal)
                {
                    return ([], null, null, stepsRefusal);
                }
                var pullRequest = item.PullRequests.Items.FirstOrDefault(candidate => candidate.Number == problem.PullRequest && !candidate.Draft && string.Equals(candidate.HeadSha, problem.HeadSha, StringComparison.Ordinal));
                if (pullRequest is null)
                {
                    return ([], null, null, ApprovalRefusal.PlanChanged);
                }
                var approval = new Approval(Guid.NewGuid(), item.Id, item.RepositoryFullName, pullRequest.Number, pullRequest.HeadSha, [.. problem.Steps.Distinct().Order()], approver.Subject, now, approver.StepUpAt, ApprovalState.Waiting, batchId);
                if (!IsRecordable(approval))
                {
                    return ([], null, null, ApprovalRefusal.PlanChanged);
                }
                approvals.Add(approval);
                if (problem.Note is not null)
                {
                    notes[approval.Id] = problem.Note;
                }
            }
        }
        if (approvals.Sum(approval => approval.Steps.Count(step => step is StepKind.Validate or StepKind.MergePullRequests)) > TaskLimits.MaxTasksPerBatch)
        {
            return ([], null, null, ApprovalRefusal.TooManyPullRequests);
        }
        // A batch of one application is named after it; one of several is a selection
        var workItemId = selection.Select(problem => problem.WorkItemId).Distinct().Count() == 1 ? selection[0].WorkItemId : $"selection:{batchId:N}";
        return (approvals, fresh => BuildBatch(batchId, workItemId, approver, now, fresh), notes, null);
    }

    private async Task<(IReadOnlyList<Approval> Candidates, Func<IReadOnlyList<Approval>, TaskBatch>? BatchFor, ApprovalRefusal? Refusal)> CandidatesAsync(
        Approver approver, string workItemId, IReadOnlyList<StepKind> ticked, IReadOnlyList<SeenPullRequest> seen, string? secondFactorCode, CancellationToken cancellationToken)
    {
        if (await FrontRefusalAsync(approver, secondFactorCode, cancellationToken) is { } front)
        {
            return ([], null, front);
        }

        var item = await catalog.FindAsync(workItemId, cancellationToken);
        if (item?.RepositoryFullName is null)
        {
            return ([], null, ApprovalRefusal.UnknownWorkItem);
        }
        if (CheckSteps(item, ticked) is { } stepsRefusal)
        {
            return ([], null, stepsRefusal);
        }
        // What is approved must be what the operator sees now: a list that is still being read, failed, or only an old read is refused.
        if (item.PullRequests is not { Status: PullRequestsStatus.Read, Stale: false })
        {
            return ([], null, ApprovalRefusal.PullRequestsNotRead);
        }

        var steps = ticked.Distinct().Order().ToArray();
        var now = time.GetUtcNow();
        var batchId = Guid.NewGuid();
        var approvals = item.PullRequests.Items
            .Where(pullRequest => !pullRequest.Draft)
            .Select(pullRequest => new Approval(Guid.NewGuid(), item.Id, item.RepositoryFullName, pullRequest.Number, pullRequest.HeadSha, steps, approver.Subject, now, approver.StepUpAt, ApprovalState.Waiting, batchId))
            .Where(IsRecordable)
            .ToList();
        if (approvals.Count == 0)
        {
            return ([], null, ApprovalRefusal.NoPullRequests);
        }
        // A batch the executor would always refuse for its size is not recorded: the operator is told now, not in the Runs screen later
        if (approvals.Count * steps.Count(step => step is StepKind.Validate or StepKind.MergePullRequests) > TaskLimits.MaxTasksPerBatch)
        {
            return ([], null, ApprovalRefusal.TooManyPullRequests);
        }
        // The same pull request twice (a list that shifted while it was read) is not a list that can be approved: read it again
        if (approvals.Select(approval => approval.PullRequest).Distinct().Count() != approvals.Count)
        {
            return ([], null, ApprovalRefusal.PlanChanged);
        }
        return seen.All(pullRequest => pullRequest is not null) && SameAsSeen(approvals, seen)
            ? (approvals, fresh => BuildBatch(batchId, item.Id, approver, now, fresh), null)
            : ([], null, ApprovalRefusal.PlanChanged);
    }

    // What is checked first, for one card and for a whole selection: the stop, who asks, and that there is a code to check
    private async Task<ApprovalRefusal?> FrontRefusalAsync(Approver approver, string? secondFactorCode, CancellationToken cancellationToken)
    {
        if (await killSwitch.IsEngagedAsync(cancellationToken))
        {
            return ApprovalRefusal.KillSwitchEngaged;
        }
        if (!approver.IsPerson)
        {
            return ApprovalRefusal.NotAPerson;
        }
        if (!approver.IsOperator)
        {
            return ApprovalRefusal.NotAnOperator;
        }
        // Whether the code is right is for the executor to say: here only that there is one, so that nobody is queued who cannot be checked
        return (secondFactor?.Required ?? taskOptions.RequireSecondFactor) && !IsCodeShaped(secondFactorCode) ? ApprovalRefusal.SecondFactorRequired : null;
    }

    // Each pull request is validated, then merged if that was ticked: the order of a batch is the order of its tasks
    private TaskBatch BuildBatch(Guid batchId, string workItemId, Approver approver, DateTimeOffset now, IReadOnlyList<Approval> approvals)
    {
        var tasks = new List<TaskSpec>();
        foreach (var approval in approvals.OrderBy(approval => approval.Repository, StringComparer.Ordinal).ThenBy(approval => approval.PullRequest))
        {
            foreach (var (step, kind) in new[] { (StepKind.Validate, TaskKind.ValidatePullRequest), (StepKind.MergePullRequests, TaskKind.MergePullRequest) })
            {
                if (approval.Steps.Contains(step))
                {
                    tasks.Add(new TaskSpec(Guid.NewGuid(), tasks.Count + 1, kind, approval.Repository, approval.PullRequest, approval.HeadSha, approval.Id));
                }
            }
        }
        return new TaskBatch(batchId, workItemId, approver.Subject, now, now + taskOptions.BatchLifetime, tasks);
    }

    private static bool IsCodeShaped(string? code) =>
        code is not null && code.Count(c => c != ' ') == 6 && code.Where(c => c != ' ').All(char.IsAsciiDigit);

    private static bool SameAsSeen(List<Approval> approvals, IReadOnlyList<SeenPullRequest> seen) =>
        seen.Count == approvals.Count
        && seen.Select(pullRequest => (pullRequest.Number, pullRequest.HeadSha)).Order().SequenceEqual(approvals.Select(approval => (approval.PullRequest, approval.HeadSha)).Order());

    private static ApprovalRefusal? CheckSteps(WorkItem item, IReadOnlyList<StepKind> ticked)
    {
        if (ticked.Count == 0)
        {
            return ApprovalRefusal.NothingTicked;
        }
        foreach (var kind in ticked.Distinct())
        {
            var step = item.Plan.FirstOrDefault(candidate => candidate.Kind == kind && candidate.EnvironmentId is null);
            if (step is not { Available: true })
            {
                return ApprovalRefusal.StepNotOffered;
            }
            if (!Supported.Contains(kind))
            {
                return ApprovalRefusal.StepNotSupported;
            }
            if (!step.Granted)
            {
                return ApprovalRefusal.StepNotGranted;
            }
        }
        return ticked.Contains(StepKind.MergePullRequests) && !ticked.Contains(StepKind.Validate) ? ApprovalRefusal.MergeNeedsValidation : null;
    }

    // The fields come from the forge and the fleet, which are untrusted: what does not fit the shapes of an order is not recorded or traced.
    private static bool IsRecordable(Approval approval) =>
        approval.PullRequest > 0
        && OrderValidation.IsValidApproval(approval.WorkItemId, approval.Repository, approval.HeadSha, approval.Steps, approval.ApproverSubject);

    // Under the gate: the check for a decision already there, the trace and the recording are one step, all or nothing.
    private async Task<ApprovalResult> RecordAsync(IReadOnlyList<Approval> candidates, Func<IReadOnlyList<Approval>, TaskBatch> batchFor, string? secondFactorCode, IReadOnlyDictionary<Guid, string>? notes, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // These checks are a cheap fast path, in front of the ledger: the ledger checks the same again inside its write, and has the last word
            var fresh = candidates.Where(approval => !store.Contains(approval)).ToList();
            if (fresh.Count == 0)
            {
                return ApprovalResult.Refused(ApprovalRefusal.AlreadyApproved);
            }
            if (store.Free < fresh.Count)
            {
                return ApprovalResult.Refused(ApprovalRefusal.StoreFull);
            }

            // The batch holds only what is new: a pull request already in an open approval is not validated or merged a second time
            var batch = batchFor(fresh);

            // The stop may have been engaged while the pull requests were read: read it again, last, before anything is written.
            if (await killSwitch.IsEngagedAsync(cancellationToken))
            {
                return ApprovalResult.Refused(ApprovalRefusal.KillSwitchEngaged);
            }

            // The journal comes first: a line without a batch is harmless, a batch without its line would be refused by the executor
            if (!await journal.TryAppendAsync(batch.Id, batch.Digest(), batch.ApproverSubject, batch.CreatedAt, cancellationToken))
            {
                return ApprovalResult.Refused(ApprovalRefusal.AuditUnavailable);
            }

            // The trace, the approvals and the batch are written together, all or nothing: no trace without its approval, no approval without its trace.
            List<AuditEntry> entries = [.. fresh.Select(approval => new AuditEntry(approval.ApprovedAt, AuditEvents.ApprovalRecorded, approval.ApproverSubject, approval.Id.ToString("D"),
                $"{approval.Repository}#{approval.PullRequest}@{approval.HeadSha} steps={string.Join(',', approval.Steps)}{(notes is not null && notes.TryGetValue(approval.Id, out var note) ? " " + note : string.Empty)}"))];
            entries.Add(new AuditEntry(batch.CreatedAt, AuditEvents.BatchQueued, batch.ApproverSubject, batch.Id.ToString("D"), $"{batch.Tasks.Count} tasks, digest {batch.Digest()[..16]}"));
            return await ledger.TryRecordAsync(fresh, batch, secondFactorCode, entries, cancellationToken) switch
            {
                LedgerResult.Recorded => new ApprovalResult(fresh, null),
                LedgerResult.AlreadyThere => ApprovalResult.Refused(ApprovalRefusal.AlreadyApproved),
                LedgerResult.StoreFull => ApprovalResult.Refused(ApprovalRefusal.StoreFull),
                LedgerResult.Stopped => ApprovalResult.Refused(ApprovalRefusal.KillSwitchEngaged),
                _ => ApprovalResult.Refused(ApprovalRefusal.AuditUnavailable),
            };
        }
        catch (StorageUnavailableException)
        {
            // A store that cannot be used is a refusal, never an error that leaves a trace behind
            return ApprovalResult.Refused(ApprovalRefusal.AuditUnavailable);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Traces a refusal that was decided before the service was reached (a row of the table that is not what the operator saw): refusals are worth a trace too.</summary>
    public Task TraceRefusalAsync(Approver approver, string workItemId, ApprovalRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(approver);
        return RecordRefusalAsync(approver, workItemId, refusal);
    }

    private async Task RecordRefusalAsync(Approver approver, string workItemId, ApprovalRefusal refusal)
    {
        // A refusal is worth a trace, but it does not depend on one: it already fails closed. The work item id comes from the client, so
        // it is recorded only if it has the shape of one.
        var subject = OrderValidation.IsValidSubject(approver.Subject) ? approver.Subject : AuditEvents.Unverified;
        if (!_refusals.TryClaim(subject, refusal))
        {
            return;
        }
        var detail = OrderValidation.IsValidWorkItem(workItemId) ? $"{refusal} {workItemId}" : refusal.ToString();
        // A refusal is short and bounded, and does not depend on the client staying connected: an abandoned request must not leave its slot claimed
        // without a trace. Whatever the outcome, a slot whose trace was not written is given back.
        var traced = false;
        try
        {
            traced = await audit.TryRecordAsync(new AuditEntry(time.GetUtcNow(), AuditEvents.ApprovalRefused, subject, null, detail), CancellationToken.None);
        }
        finally
        {
            if (!traced)
            {
                _refusals.Release(subject, refusal);
            }
        }
    }
}
