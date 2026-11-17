namespace Orchestrarr.Core.Missions;

/// <summary>The three places where a mission happens, in the order a change travels (FUNCTIONAL 5.4, ADR 0030). The order is meaningful: a power that reaches a world reaches the ones before it only if it says so.</summary>
public enum World
{
    /// <summary>A workspace in the staging environment: the application is deployed from the branch, changed, rebuilt and tested.</summary>
    Staging = 0,

    /// <summary>The forge: branch, pull request, CI/CD, merge into the integration branch.</summary>
    Git = 1,

    /// <summary>Production: deployment through the repository's declared delivery, and verification after it. The way back is a new change made through the process, never an act (ADR 0040).</summary>
    Release = 2,
}

/// <summary>
/// The acts that have consequences outside a workspace. The worker agent asks for them; the runner or the control plane performs them, and only when the mandate lists
/// them and the preconditions hold (ADR 0033).
/// </summary>
public enum GatedAct
{
    OpenPullRequest = 0,
    MergePullRequest = 1,
    Deploy = 2,

    // 3 is retired (it was the rollback, ADR 0040): never reuse it, an old signed mandate or a stored row could read it as the new act
    // 4 to 6 are retired too (ADR 0041 point 3): the platform owns the slots of the staging and no mandate lists them; the values are never reused
    ReserveStaging = 4,
    RestoreStaging = 5,
    ReleaseStaging = 6,
}
