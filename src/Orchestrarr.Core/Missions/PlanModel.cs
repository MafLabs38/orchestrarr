namespace Orchestrarr.Core.Missions;

/// <summary>Where a problem is in its life (ARCHITECTURE section 4).</summary>
public enum ProblemState
{
    InPlan,
    InProgress,
    Closed,
    Failed,
    SetAside,
}

/// <summary>Who validated a plan entry: a person (an approval with the second factor), or a standing rule.</summary>
public sealed record Validation(string By, string? RuleId);

/// <summary>
/// A problem in the plan with the limits it may be solved within (FR-PLAN-2): how far it may go, the budget, the repositories. The plan entry is the first source of power; the
/// grant of the application and the standing rule are the others (see <see cref="EffectivePower"/>).
/// </summary>
public sealed record PlanEntry(
    string ProblemId,
    string ApplicationId,
    string ProblemClass,
    string Objective,
    IReadOnlyList<string> Repositories,
    World MaxWorld,
    MissionBudget Budget,
    Validation Validation,
    DateTimeOffset ValidatedAt);

/// <summary>What the control plane keeps about one problem while it is worked on: its plan entry, the highest world its powers reach, and whether its application has a staging.</summary>
public sealed record ProblemRun(string ProblemId, string ApplicationId, ProblemState State, DateTimeOffset StartedAt, PlanEntry Entry, World HighestAllowed, bool UsesStaging, World? CurrentWorld = null, string? Note = null);

/// <summary>The stores of the control plane, behind interfaces so that SQLite and memory give the same answers.</summary>
public interface IMissionStore
{
    Mission? Get(string id);

    void Save(Mission mission);

    IReadOnlyList<Mission> ForProblem(string problemId);
}

public interface IMandateLedger
{
    void Record(SignedMandate mandate);

    SignedMandate? Get(string id);

    int CountForProblem(string problemId);

    void Revoke(string id);

    bool IsRevoked(string id);
}

public interface IProblemStore
{
    ProblemRun? Get(string problemId);

    void Save(ProblemRun run);
}

public interface IDossierStore
{
    void Save(Dossier dossier);

    Dossier? Get(string missionId);
}

/// <summary>The control plane fetches the facts itself (FR-MAN-7): the diff and the CI status from the forge on the exact commit, the health from Portarr.</summary>
public interface IFactsSource
{
    PreconditionFacts Facts(string problemId, World world, Dossier? dossier);
}

/// <summary>Which runner serves a world for an application (a runner profile on a host).</summary>
public interface IRunnerDirectory
{
    string RunnerFor(World world, string applicationId);
}
