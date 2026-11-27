namespace Orchestrarr.Core.Missions;

/// <summary>
/// The rank of an operation in the route of its lot (ADR 0055): what **changes what the others run on** goes first, so that a batch ticked in any order is done in the order that works. (0) a **pipeline that is red** on the main branch (nothing of the lot merges until it is green); (1) the **runtime**: the base image
/// that a container is built on, which fixes the version of the language; (2) the **security alerts**, whose fixes need that runtime; (3) the **updates of libraries and pipeline tools**, pull requests of a bot; (4) the rest (a
/// moving tag, a code or a CI problem). It is a default for the kinds of operation the product knows, ordered **after the priority** (ADR 0050) and before the arrival; it is not a graph of dependencies.
/// </summary>
public static class RouteRank
{
    /// <summary>A pipeline that is red on the main branch: nothing else of the lot can be merged until it is green, so it is mended first.</summary>
    public const int Pipeline = 0;
    public const int Runtime = 1;
    public const int Alert = 2;
    public const int Update = 3;
    public const int Other = 4;

    /// <summary>The rank of an operation, from the kind and the nature that the control plane read for it (as words in the brief of the plan entry).</summary>
    public static int Of(PlanEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!Enum.TryParse<Decisions.ProblemKind>(entry.Brief?.Kind, out var kind))
        {
            return Other;
        }
        return Of(kind, Enum.TryParse<Decisions.ProblemNature>(entry.Brief?.Nature, out var nature) ? nature : Decisions.ProblemNature.Other);
    }

    /// <summary>
    /// The rank of a kind of problem and its nature (ADR 0054): the runtime is the **base image** of the application (the <c>FROM</c> of its Dockerfile), whether a free agent raises it (an image row) or a bot's pull request does
    /// (a Docker update); another image (a service of a compose stack, a sidecar) changes nothing that the others run on, so it is in the rest.
    /// </summary>
    public static int Of(Decisions.ProblemKind kind, Decisions.ProblemNature nature) => (kind, nature) switch
    {
        (Decisions.ProblemKind.FailingCi, _) => Pipeline,
        (_, Decisions.ProblemNature.BaseImageUpdate) => Runtime,
        (Decisions.ProblemKind.DependencyAlert, _) => Alert,
        (Decisions.ProblemKind.DependencyUpdate, _) => Update,
        _ => Other,
    };
}
