using Orchestrarr.Core.Decisions;

namespace Orchestrarr.Core.Missions;

/// <summary>What a push credential may do for an operation, and why, in words that go to the audit trail (ADR 0057).</summary>
public sealed record PushGrant(bool Pipelines, string Justification);

/// <summary>
/// **The control plane justifies the permission to change a pipeline** (ADR 0057, the maintainer, 2026-11-27): a free agent pushes its branch with a credential that the control plane makes for its mission, and that credential carries
/// the permission on the pipeline files only when **both** say so: the operation is about its pipeline, as the control plane read the row (<see cref="PipelineChanges.MayChange"/>), and the frame of the mission, which falls inside the
/// approval the maintainer signed, leaves the pipeline files open. Anything else gets a credential with which the forge itself refuses a push that touches a workflow, before any of it runs. Pure.
/// </summary>
public static class PushGrants
{
    // A path that stands for every workflow of the forge: the frame forbids it when it forbids the pipelines
    private const string AWorkflow = ".github/workflows/pipeline.yml";

    public static PushGrant For(PlanEntry entry, MissionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(frame);
        if (!PipelineChanges.MayChange(entry))
        {
            return new PushGrant(false, "the operation is not about its pipeline: the credential cannot change a workflow");
        }
        if (PathPatterns.AnyMatches(frame.ForbiddenPaths, [AWorkflow]))
        {
            return new PushGrant(false, "the frame of the mission forbids the pipeline files: the credential cannot change a workflow");
        }
        var about = entry.Brief?.Kind == nameof(ProblemKind.FailingCi) ? "a red pipeline" : $"a {entry.Brief?.Nature ?? "change"} that the pipeline follows";
        return new PushGrant(true, $"the operation is about its pipeline ({about}) and the approved frame of the mission leaves the pipeline files open");
    }
}
