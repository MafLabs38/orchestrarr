using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.Forge;

namespace Orchestrarr.Core.WorkItems;

public enum Severity
{
    Info,
    Warning,
    Danger,
}

public enum FindingKind
{
    DependencyAlerts,
    CodeScanningAlerts,
    SecretScanningAlerts,
    OpenDependencyPullRequests,
    FailingCi,
    OutdatedImage,

    /// <summary>The image is run under a tag that moves (latest): nobody can tell which version it is, or whether it is behind.</summary>
    FloatingImage,

    /// <summary>The dependency alert scan is off or unreadable: the repository is not known to be clean.</summary>
    DependencyScanUnavailable,
}

/// <summary>The outdated image behind an <see cref="FindingKind.OutdatedImage"/> finding.</summary>
public sealed record ImageUpdate(string Reference, string? Current, string? Latest, ImageChange Change);

/// <summary>
/// What is wrong, as data: the interface words it. <c>Count</c> is the number of alerts, pull requests or secrets; <c>Alerts</c> its
/// breakdown by severity for alert findings; <c>Image</c> the update for an outdated image.
/// </summary>
public sealed record Finding(FindingKind Kind, Severity Severity, int Count, AlertCounts? Alerts = null, ImageUpdate? Image = null);

public enum StepKind
{
    Validate,
    MergePullRequests,
    MergeFix,
    Deploy,

    /// <summary>An agent tries to repair a pull request whose checks fail; the patch is shown, nothing is pushed. It stands alone: it validates by itself first.</summary>
    Repair,
}

/// <summary>Why a step cannot be done.</summary>
public enum StepReason
{
    /// <summary>The environment description does not say where this runs.</summary>
    NoPlacement,
}

/// <summary>
/// One step of a plan, as a checkbox of the decision card (SPEC D-04). <c>Available</c>: possible, from what the source says.
/// <c>Granted</c>: allowed by the powers granted to Orchestrarr (SPEC D-10, D-11), which are none yet. <c>Ticked</c>: ticked by default
/// (SPEC D-16), unless the step cannot be done. <c>Reason</c> says why a step is not available.
/// </summary>
public sealed record PlanStep(
    StepKind Kind,
    string? EnvironmentId,
    string? EnvironmentName,
    bool Ticked,
    bool Available,
    bool Granted,
    StepReason? Reason);

/// <summary>
/// A unit of maintenance work on one repository or one third-party image: its findings and the plan the operator would approve. An empty
/// plan means nothing can be done automatically (for example an exposed secret, which only a person can revoke). <c>PullRequests</c>: the
/// open dependency pull requests read from the forge, and the state of that reading.
/// </summary>
public sealed record WorkItem(
    string Id,
    string Title,
    string? RepositoryFullName,
    IReadOnlyList<string> AppIds,
    Severity Severity,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<PlanStep> Plan,
    IReadOnlyList<Placement> Placements,
    PullRequestsRead PullRequests,
    DateTimeOffset? LastPushAt = null);

public enum PullRequestsStatus
{
    /// <summary>Nothing to read: no pull request is waiting.</summary>
    NotNeeded,

    /// <summary>No forge is connected: they cannot be read.</summary>
    NotConnected,

    /// <summary>Being read, and not known yet.</summary>
    Pending,
    Read,

    /// <summary>Reading failed. <c>Items</c> then holds the last good read, if any, and <c>Stale</c> says so.</summary>
    Failed,
}

/// <summary>The pull requests of a work item and the state of their reading: one value, so that impossible combinations cannot exist.</summary>
public sealed record PullRequestsRead(
    PullRequestsStatus Status,
    IReadOnlyList<PullRequestInfo> Items,
    bool Truncated = false,
    SourceError? Error = null,
    bool Stale = false)
{
    public static PullRequestsRead NotNeeded { get; } = new(PullRequestsStatus.NotNeeded, []);

    public static PullRequestsRead NotConnected { get; } = new(PullRequestsStatus.NotConnected, []);

    public static PullRequestsRead Pending { get; } = new(PullRequestsStatus.Pending, []);

    public static PullRequestsRead Of(PullRequestList list) => new(PullRequestsStatus.Read, list.Items, list.Truncated);

    public static PullRequestsRead Failure(SourceError error, PullRequestsRead? previous = null) =>
        new(PullRequestsStatus.Failed, previous?.Items ?? [], previous?.Truncated ?? false, error, Stale: previous is { Items.Count: > 0 });
}

/// <summary>What Orchestrarr may do (SPEC D-10, D-11). Powers are off until the operator enables them.</summary>
public interface IPowerPolicy
{
    bool IsGranted(StepKind step, string? repositoryFullName, string? environmentId);
}

/// <summary>No power is granted: the policy of a read-only Orchestrarr.</summary>
public sealed class NoPowers : IPowerPolicy
{
    public bool IsGranted(StepKind step, string? repositoryFullName, string? environmentId) => false;
}
