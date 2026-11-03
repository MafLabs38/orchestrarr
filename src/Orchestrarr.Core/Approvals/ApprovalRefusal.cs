namespace Orchestrarr.Core.Approvals;

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

    /// <summary>The pull requests the operator saw are not the ones there now (a push, a new or a closed pull request): look again, then approve.</summary>
    PlanChanged,

    /// <summary>Merging without validating first: a fix is only merged after the checks (VISION, verify then report).</summary>
    MergeNeedsValidation,
    NoPullRequests,
    AlreadyApproved,
    StoreFull,
    AuditUnavailable,
}
