using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Approvals;

/// <summary>
/// A decision of the operator, recorded: these steps, on this pull request, at this commit (SPEC D-37). It is not yet an order: the
/// signed order is issued when the run is about to start, so that a wait for the workshop never lets it expire (ADR 0007). A bot that
/// rewrites its branch after the tick changes the commit, and this approval no longer covers it. <c>StepUpAt</c> keeps when the approver
/// last passed a second factor, so that the order issued from this approval can carry it (SPEC D-47). A draft pull request is never approved.
/// </summary>
public sealed record Approval(
    Guid Id,
    string WorkItemId,
    string Repository,
    int PullRequest,
    string HeadSha,
    IReadOnlyList<StepKind> Steps,
    string ApproverSubject,
    DateTimeOffset ApprovedAt,
    DateTimeOffset? StepUpAt = null,
    ApprovalState State = ApprovalState.Waiting,
    Guid? BatchId = null,
    DateTimeOffset? EndedAt = null)
{
    /// <summary>Whether this approval can still be carried out. An ended one never counts as "already approved": the operator may approve again.</summary>
    public bool IsOpen => State is ApprovalState.Waiting or ApprovalState.Taken;
}

/// <summary>
/// Where an approval is in its life (ADR 0013, 0017). <c>DryRunDone</c> is a real end: the runner checked everything and wrote what it
/// would have started, which spends the approval, so a real run needs a new approval.
/// </summary>
public enum ApprovalState
{
    Waiting,
    Taken,
    DryRunDone,
    Refused,
    Expired,
}
