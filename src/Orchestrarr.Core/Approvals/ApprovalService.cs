using Orchestrarr.Core.Launch;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Approvals;

/// <summary>
/// Records the operator's approval of a decision card (SPEC F-CRD-01, D-37, D-52). The server rebuilds everything from the current work
/// item; what the client sends is the work item, the steps it ticked and the pull requests (number and commit) it displayed, which must be
/// exactly the ones there now: an approval covers what the operator saw, never a commit that arrived after. Nothing here runs anything; an
/// approval waits for the launcher. It fails closed: the kill switch, an unreadable or changed pull request list, an unrecordable trace each
/// refuse. A draft pull request is never approved.
/// </summary>
public sealed class ApprovalService(
    WorkItemCatalog catalog,
    IApprovalStore store,
    IKillSwitch killSwitch,
    IAuditSink audit,
    TimeProvider time,
    IApprovalLedger? ledger = null) : IDisposable
{
    // Where the approvals and their trace are written together; the sandbox's stores in memory when nothing else is given
    private readonly IApprovalLedger _ledger = ledger ?? new InMemoryApprovalLedger(store, audit);

    // What this vertical records. The other steps are shown and can be granted, but run with later verticals.
    private static readonly StepKind[] Supported = [StepKind.Validate, StepKind.MergePullRequests];

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ApprovalResult> ApproveAsync(
        Approver approver,
        string workItemId,
        IReadOnlyList<StepKind> ticked,
        IReadOnlyList<SeenPullRequest> seen,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(ticked);
        ArgumentNullException.ThrowIfNull(seen);

        // Everything that reads or checks comes first, without the gate: a slow read of the forge must not block the other approvals.
        var (candidates, refusal) = await CandidatesAsync(approver, workItemId, ticked, seen, cancellationToken);
        var result = refusal is null
            ? await RecordAsync(candidates, cancellationToken)
            : ApprovalResult.Refused(refusal.Value);
        if (!result.Recorded)
        {
            await RecordRefusalAsync(approver, workItemId, result.Refusal!.Value, cancellationToken);
        }
        return result;
    }

    private async Task<(IReadOnlyList<Approval> Candidates, ApprovalRefusal? Refusal)> CandidatesAsync(
        Approver approver, string workItemId, IReadOnlyList<StepKind> ticked, IReadOnlyList<SeenPullRequest> seen, CancellationToken cancellationToken)
    {
        if (await killSwitch.IsEngagedAsync(cancellationToken))
        {
            return ([], ApprovalRefusal.KillSwitchEngaged);
        }
        if (!approver.IsPerson)
        {
            return ([], ApprovalRefusal.NotAPerson);
        }
        if (!approver.IsOperator)
        {
            return ([], ApprovalRefusal.NotAnOperator);
        }

        var item = await catalog.FindAsync(workItemId, cancellationToken);
        if (item?.RepositoryFullName is null)
        {
            return ([], ApprovalRefusal.UnknownWorkItem);
        }
        if (CheckSteps(item, ticked) is { } stepsRefusal)
        {
            return ([], stepsRefusal);
        }
        // What is approved must be what the operator sees now: a list that is still being read, failed, or only an old read is refused.
        if (item.PullRequests is not { Status: PullRequestsStatus.Read, Stale: false })
        {
            return ([], ApprovalRefusal.PullRequestsNotRead);
        }

        var steps = ticked.Distinct().Order().ToArray();
        var now = time.GetUtcNow();
        var approvals = item.PullRequests.Items
            .Where(pullRequest => !pullRequest.Draft)
            .Select(pullRequest => new Approval(Guid.NewGuid(), item.Id, item.RepositoryFullName, pullRequest.Number, pullRequest.HeadSha, steps, approver.Subject, now, approver.StepUpAt))
            .Where(IsRecordable)
            .ToList();
        if (approvals.Count == 0)
        {
            return ([], ApprovalRefusal.NoPullRequests);
        }
        // The same pull request twice (a list that shifted while it was read) is not a list that can be approved: read it again
        if (approvals.Select(approval => approval.PullRequest).Distinct().Count() != approvals.Count)
        {
            return ([], ApprovalRefusal.PlanChanged);
        }
        return seen.All(pullRequest => pullRequest is not null) && SameAsSeen(approvals, seen) ? (approvals, null) : ([], ApprovalRefusal.PlanChanged);
    }

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
    private async Task<ApprovalResult> RecordAsync(IReadOnlyList<Approval> candidates, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var fresh = candidates.Where(approval => !store.Contains(approval)).ToList();
            if (fresh.Count == 0)
            {
                return ApprovalResult.Refused(ApprovalRefusal.AlreadyApproved);
            }
            if (store.Free < fresh.Count)
            {
                return ApprovalResult.Refused(ApprovalRefusal.StoreFull);
            }

            // The stop may have been engaged while the pull requests were read: read it again, last, before anything is written.
            if (await killSwitch.IsEngagedAsync(cancellationToken))
            {
                return ApprovalResult.Refused(ApprovalRefusal.KillSwitchEngaged);
            }

            // The trace and the approvals are written together, all or nothing: no trace without its approval, no approval without its trace.
            var entries = fresh.Select(approval => new AuditEntry(approval.ApprovedAt, AuditEvents.ApprovalRecorded, approval.ApproverSubject, approval.Id.ToString("D"),
                $"{approval.Repository}#{approval.PullRequest}@{approval.HeadSha} steps={string.Join(',', approval.Steps)}")).ToList();
            return await _ledger.TryRecordAsync(fresh, entries, cancellationToken) switch
            {
                LedgerResult.Recorded => new ApprovalResult(fresh, null),
                LedgerResult.AlreadyThere => ApprovalResult.Refused(ApprovalRefusal.AlreadyApproved),
                LedgerResult.StoreFull => ApprovalResult.Refused(ApprovalRefusal.StoreFull),
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

    private async Task RecordRefusalAsync(Approver approver, string workItemId, ApprovalRefusal refusal, CancellationToken cancellationToken)
    {
        // A refusal is worth a trace, but it does not depend on one: it already fails closed. The work item id comes from the client, so
        // it is recorded only if it has the shape of one.
        var subject = OrderValidation.IsValidSubject(approver.Subject) ? approver.Subject : AuditEvents.Unverified;
        var detail = OrderValidation.IsValidWorkItem(workItemId) ? $"{refusal} {workItemId}" : refusal.ToString();
        await audit.TryRecordAsync(new AuditEntry(time.GetUtcNow(), AuditEvents.ApprovalRefused, subject, null, detail), cancellationToken);
    }
}
