using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Approvals;

/// <summary>
/// A decision of the operator, recorded: these steps, on this pull request, at this commit (SPEC D-37). It is not yet an order: the
/// signed order is issued when the run is about to start, so that a wait for the workshop never lets it expire (ADR 0007). A bot that
/// rewrites its branch after the tick changes the commit, and this approval no longer covers it.
/// </summary>
public sealed record Approval(
    Guid Id,
    string WorkItemId,
    string Repository,
    int PullRequest,
    string HeadSha,
    IReadOnlyList<StepKind> Steps,
    string ApproverSubject,
    DateTimeOffset ApprovedAt);

/// <summary>Why an approval was not recorded. A code, not a sentence (SPEC D-22).</summary>
public enum ApprovalRefusal
{
    KillSwitchEngaged,
    NotAPerson,
    NotAnOperator,
    UnknownWorkItem,

    /// <summary>The pull requests of the work item are not read, or only an old read is left: what is approved must be what is shown now.</summary>
    PullRequestsNotRead,
    NothingTicked,

    /// <summary>A ticked step is not in the plan, or cannot be done.</summary>
    StepNotOffered,

    /// <summary>A ticked step is not granted to Orchestrarr (SPEC D-10, D-11).</summary>
    StepNotGranted,

    /// <summary>The step exists but this vertical does not run it yet.</summary>
    StepNotSupported,

    /// <summary>Merging without validating first: a fix is only merged after the checks (VISION, verify then report).</summary>
    MergeNeedsValidation,
    NoPullRequests,
    AlreadyApproved,
    StoreFull,
    AuditUnavailable,
}

public sealed record ApprovalResult(IReadOnlyList<Approval> Approvals, ApprovalRefusal? Refusal)
{
    public bool Recorded => Refusal is null;

    public static ApprovalResult Refused(ApprovalRefusal refusal) => new([], refusal);
}
