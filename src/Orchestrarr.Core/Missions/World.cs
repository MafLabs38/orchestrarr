namespace Orchestrarr.Core.Missions;

/// <summary>The three places where a mission happens, in the order a change travels (FUNCTIONAL 5.4, ADR 0030). The order is meaningful: a power that reaches a world reaches the ones before it only if it says so.</summary>
public enum World
{
    /// <summary>A workspace in the staging environment: the application is deployed from the branch, changed, rebuilt and tested.</summary>
    Staging = 0,

    /// <summary>The forge: branch, pull request, CI/CD, merge into the integration branch.</summary>
    Git = 1,

    /// <summary>Production: deployment, verification, rollback.</summary>
    Release = 2,
}

/// <summary>
/// The acts that have consequences outside a workspace. The worker agent asks for them; the runner or the control plane performs them, and only when the mandate lists
/// them and the preconditions hold (ADR 0033).
/// </summary>
public enum GatedAct
{
    OpenPullRequest,
    MergePullRequest,
    Deploy,
    Rollback,
    ReserveStaging,
    RestoreStaging,
    ReleaseStaging,
}
