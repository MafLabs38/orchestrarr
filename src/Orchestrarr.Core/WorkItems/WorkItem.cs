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
/// open dependency pull requests read from the forge; null when they were not read (no forge connected, or none to read), in which case
/// <c>PullRequestsError</c> says whether reading them failed.
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
    IReadOnlyList<PullRequestInfo>? PullRequests = null,
    SourceError? PullRequestsError = null);

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
