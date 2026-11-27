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
    // The folder of the forge's workflows: a credential that may write one may write them all
    private const string WorkflowFolder = ".github/workflows/";

    public static PushGrant For(PlanEntry entry, MissionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(frame);
        if (!PipelineChanges.MayChange(entry))
        {
            return new PushGrant(false, "the operation is not about its pipeline: the credential cannot change a workflow");
        }
        if (frame.ForbiddenPaths.Any(ForbidsAWorkflow))
        {
            return new PushGrant(false, "the frame of the mission forbids the pipeline files: the credential cannot change a workflow");
        }
        var about = entry.Brief?.Kind == nameof(ProblemKind.FailingCi) ? "a red pipeline" : $"a {entry.Brief?.Nature ?? "change"} that the pipeline follows";
        return new PushGrant(true, $"the operation is about its pipeline ({about}) and the approved frame of the mission leaves the pipeline files open");
    }

    /// <summary>
    /// Whether a forbidden pattern can match a workflow, so that a frame with it gets no permission on the workflows (one rule, decided on the pattern): it names the folder of the workflows; or it matches a workflow whatever its name
    /// (<c>**</c>, <c>**/*.yml</c>, <c>.github/**</c>); or it reaches into the forge's folder through a wildcard (<c>.github/*/ci.yml</c>, <c>.github/work*/**</c>), which may be that folder.
    /// </summary>
    internal static bool ForbidsAWorkflow(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Contains(WorkflowFolder.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) || PathPatterns.AnyMatches([pattern], [$"{WorkflowFolder}any.yml", $"{WorkflowFolder}any.yaml"]))
        {
            return true;
        }
        var segments = pattern.Split('/');
        return segments.Length >= 2 && segments[0] == ".github" && segments[1].Contains('*', StringComparison.Ordinal);
    }
}
