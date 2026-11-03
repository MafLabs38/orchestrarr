using Orchestrarr.Core.Launch;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Approvals;

public static class ApprovalAuditEvents
{
    public const string Recorded = "approval.recorded";
    public const string Refused = "approval.refused";
    public const string KillSwitchEngaged = "killswitch.engaged";
    public const string KillSwitchReleased = "killswitch.released";
}

/// <summary>
/// Records the operator's approval of a decision card (SPEC F-CRD-01, D-37). The server rebuilds everything from the current work item:
/// what the client sends is only the work item and the steps it ticked, never a commit, a repository or a granted power. Nothing here
/// runs anything; an approval waits for the launcher. It fails closed: the kill switch, an unreadable pull request list, an unrecordable
/// trace each refuse.
/// </summary>
public sealed class ApprovalService(
    WorkItemCatalog catalog,
    IApprovalStore store,
    IKillSwitch killSwitch,
    IAuditSink audit,
    TimeProvider time)
{
    // What this vertical records. The other steps are shown and can be granted, but run with later verticals.
    private static readonly StepKind[] Supported = [StepKind.Validate, StepKind.MergePullRequests];

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ApprovalResult> ApproveAsync(Approver approver, string workItemId, IReadOnlyList<StepKind> ticked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(ticked);

        // One approval at a time: the checks for a duplicate and the recording are one step.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var result = await DecideAsync(approver, workItemId, ticked, cancellationToken);
            if (!result.Recorded)
            {
                await RecordRefusalAsync(approver, workItemId, result.Refusal!.Value, cancellationToken);
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ApprovalResult> DecideAsync(Approver approver, string workItemId, IReadOnlyList<StepKind> ticked, CancellationToken cancellationToken)
    {
        if (await killSwitch.IsEngagedAsync(cancellationToken))
        {
            return ApprovalResult.Refused(ApprovalRefusal.KillSwitchEngaged);
        }
        if (!approver.IsPerson)
        {
            return ApprovalResult.Refused(ApprovalRefusal.NotAPerson);
        }
        if (!approver.IsOperator)
        {
            return ApprovalResult.Refused(ApprovalRefusal.NotAnOperator);
        }

        var item = await catalog.FindAsync(workItemId, cancellationToken);
        if (item?.RepositoryFullName is null)
        {
            return ApprovalResult.Refused(ApprovalRefusal.UnknownWorkItem);
        }

        var stepsRefusal = CheckSteps(item, ticked);
        if (stepsRefusal is not null)
        {
            return ApprovalResult.Refused(stepsRefusal.Value);
        }
        // What is approved must be what the operator sees now: a list that is still being read, failed, or only an old read is refused.
        if (item.PullRequests is not { Status: PullRequestsStatus.Read, Stale: false })
        {
            return ApprovalResult.Refused(ApprovalRefusal.PullRequestsNotRead);
        }

        var steps = ticked.Distinct().Order().ToArray();
        var now = time.GetUtcNow();
        var approvals = item.PullRequests.Items
            .Where(pullRequest => !pullRequest.Draft)
            .Select(pullRequest => new Approval(Guid.NewGuid(), item.Id, item.RepositoryFullName, pullRequest.Number, pullRequest.HeadSha, steps, approver.Subject, now))
            .Where(IsRecordable)
            .ToList();
        if (approvals.Count == 0)
        {
            return ApprovalResult.Refused(ApprovalRefusal.NoPullRequests);
        }
        return await RecordAsync(approvals, now, cancellationToken);
    }

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
        OrderValidation.IsValid(approval.WorkItemId, approval.Repository, approval.HeadSha, null, approval.Steps, approval.ApproverSubject, "approvals")
        && approval.PullRequest > 0;

    private async Task<ApprovalResult> RecordAsync(List<Approval> approvals, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var fresh = approvals.Where(approval => !Exists(approval)).ToList();
        if (fresh.Count == 0)
        {
            return ApprovalResult.Refused(ApprovalRefusal.AlreadyApproved);
        }

        // The trace first: no trace, no approval.
        foreach (var approval in fresh)
        {
            var entry = new AuditEntry(now, ApprovalAuditEvents.Recorded, approval.ApproverSubject, approval.Id.ToString("D"),
                $"{approval.Repository}#{approval.PullRequest}@{approval.HeadSha} steps={string.Join(',', approval.Steps)}");
            if (!await audit.TryRecordAsync(entry, cancellationToken))
            {
                return ApprovalResult.Refused(ApprovalRefusal.AuditUnavailable);
            }
        }

        var added = new List<Approval>();
        foreach (var approval in fresh)
        {
            if (store.TryAdd(approval) == ApprovalStoreResult.Full)
            {
                return added.Count == 0 ? ApprovalResult.Refused(ApprovalRefusal.StoreFull) : new ApprovalResult(added, null);
            }
            added.Add(approval);
        }
        return new ApprovalResult(added, null);
    }

    private bool Exists(Approval candidate) =>
        store.List().Any(existing => existing.Repository == candidate.Repository
            && existing.PullRequest == candidate.PullRequest
            && existing.HeadSha == candidate.HeadSha
            && existing.Steps.Order().SequenceEqual(candidate.Steps.Order()));

    private async Task RecordRefusalAsync(Approver approver, string workItemId, ApprovalRefusal refusal, CancellationToken cancellationToken)
    {
        // A refusal is worth a trace, but it does not depend on one: it already fails closed. The work item id comes from the client, so
        // it is recorded only if it has the shape of one.
        var subject = OrderValidation.IsValidSubject(approver.Subject) ? approver.Subject : "unverified";
        var detail = OrderValidation.IsValidWorkItem(workItemId) ? $"{refusal} {workItemId}" : refusal.ToString();
        await audit.TryRecordAsync(new AuditEntry(time.GetUtcNow(), ApprovalAuditEvents.Refused, subject, null, detail), cancellationToken);
    }
}
