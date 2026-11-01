using Orchestrarr.Core.Fleet;

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
}

public sealed record Finding(FindingKind Kind, Severity Severity, int Count, string Summary);

public enum StepKind
{
    Validate,
    Merge,
    Deploy,
}

/// <summary>
/// One step of a plan, as a checkbox of the decision card (SPEC D-04). <c>Ticked</c>: ticked by default, unless the step cannot be
/// offered (SPEC D-16). <c>Available</c>: false when Orchestrarr cannot do the step, and <c>Reason</c> says why.
/// </summary>
public sealed record PlanStep(StepKind Kind, string Label, string? EnvironmentId, bool Ticked, bool Available, string? Reason);

/// <summary>A unit of maintenance work on one repository or one image: its findings and the plan the operator would approve.</summary>
public sealed record WorkItem(
    string Id,
    string Title,
    string? RepositoryFullName,
    IReadOnlyList<string> AppIds,
    Severity Severity,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<PlanStep> Plan,
    IReadOnlyList<Placement> Placements);
