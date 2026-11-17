namespace Orchestrarr.Core.Missions;

/// <summary>Where a problem is in its life (ARCHITECTURE section 4).</summary>
public enum ProblemState
{
    InPlan,
    InProgress,

    /// <summary>The controller escalated: a decision that belongs to the maintainer, who is told. It waits; it is not a failure.</summary>
    Escalated,
    Closed,
    Failed,
    SetAside,
}

/// <summary>Who validated a plan entry: a person (an approval with the second factor), or a standing rule.</summary>
public sealed record Validation(string By, string? RuleId);

/// <summary>
/// A problem in the plan with the limits it may be solved within (FR-PLAN-2): how far it may go, the budget, the repository (**exactly one**: a problem is on one codebase, and a problem on several repositories is as many problems, ADR 0041). The plan entry is the first source of power; the
/// grant of the application and the certificate are the others (see <see cref="EffectivePower"/>).
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

/// <summary>What the control plane keeps about one problem while it is worked on: its plan entry, the certificate of the approval, the highest world its powers reached when it started (a display: the power is read again at each crossing), and whether its application has a staging.</summary>
public sealed record ProblemRun(string ProblemId, string ApplicationId, ProblemState State, DateTimeOffset StartedAt, PlanEntry Entry, World HighestAllowed, bool UsesStaging, SignedApproval Approval, World? CurrentWorld = null, string? Note = null);

/// <summary>The four stores of the missions, together: the missions, the mandates, the problems and the dossiers.</summary>
public sealed record MissionStores(IMissionStore Missions, IMandateLedger Mandates, IProblemStore Problems, IDossierStore Dossiers);

/// <summary>What the power of a problem is read from, at every crossing and every act: the approval source, the verifier of its certificates, the withdrawals the authority published, the grants and the global stop.</summary>
public sealed record PowerSources(IApprovalSource Approvals, ApprovalVerifier Verifier, IApprovalWithdrawals Withdrawals, IGrantSource Grants, IStopSignal Stop);

/// <summary>The stores of the control plane, behind interfaces so that SQLite and memory give the same answers.</summary>
public interface IMissionStore
{
    Mission? Get(string id);

    void Save(Mission mission);

    IReadOnlyList<Mission> ForProblem(string problemId);

    /// <summary>The newest missions first, for the screens.</summary>
    IReadOnlyList<Mission> Recent(int limit);
}

public interface IMandateLedger
{
    void Record(SignedMandate mandate);

    SignedMandate? Get(string id);

    /// <summary>Several mandates in one read, by identifier (the list of the missions reads a page at a time): the ones that exist, keyed by identifier.</summary>
    IReadOnlyDictionary<string, SignedMandate> GetMany(IReadOnlyCollection<string> ids);

    /// <summary>Which of these mandates were revoked, in one read.</summary>
    IReadOnlySet<string> RevokedAmong(IReadOnlyCollection<string> ids);

    int CountForProblem(string problemId);

    /// <summary>How many mandates one run of a problem was given: the mandates of the problem under the approval of that run (a problem started again on a new approval starts a new count).</summary>
    int CountForRun(string problemId, string approvalId);

    /// <summary>How many mandates were issued under one certificate of the authority.</summary>
    int CountForApproval(string approvalId);

    void Revoke(string id);

    bool IsRevoked(string id);
}

public interface IProblemStore
{
    ProblemRun? Get(string problemId);

    /// <summary>Several problems in one read, by identifier: the ones that exist, keyed by identifier.</summary>
    IReadOnlyDictionary<string, ProblemRun> GetMany(IReadOnlyCollection<string> problemIds);

    void Save(ProblemRun run);

    IReadOnlyList<ProblemRun> InProgress();
}

/// <summary>Where the locks of the problems live: one object per problem, shared by every conductor that works on the same stores (the sweep and the demo included).</summary>
public sealed class ProblemLocks
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);

    public object Of(string problemId) => _locks.GetOrAdd(problemId, _ => new object());
}

public interface IDossierStore
{
    void Save(Dossier dossier);

    Dossier? Get(string missionId);

    /// <summary>Several dossiers in one read, by mission identifier: the ones that exist, keyed by mission identifier.</summary>
    IReadOnlyDictionary<string, Dossier> GetMany(IReadOnlyCollection<string> missionIds);
}

/// <summary>What the control plane fetched itself (FR-MAN-7; <c>ChangedPaths</c> are **the whole change from the merge base with the target branch to the head commit the source read, with both sides of a rename or a copy and every deleted path**, or null when they are not known: the conductor matches them against the frame of the mission, which holds the certificate's floor, so a source never decides what is forbidden): the diff and the CI status from the forge on the exact commit, the health from Portarr. A fact that is not known is null, and counts as failing.</summary>
public sealed record ObservedFacts(
    bool BudgetAvailable, bool? DiffIsClean, bool? CiGreenOnExactCommit, bool? ArtifactIsTheOneProvedInStaging, bool? WayBackKnown, bool WorkerClaimMatchesFacts,
    bool? MergeMayDeploy = null, bool? ExecutedFilesTouched = null, bool? TagCreationBlocked = null, bool? BranchCannotReachWriteCredentials = null, string? HeadCommit = null, bool? IdentityConfinedToItsBranches = null, IReadOnlyList<string>? ChangedPaths = null, string? Repository = null, string? BaseCommit = null);

/// <summary>
/// The source of the facts. It says only what it observed: the plan, the stop, the loop and the staging are the conductor's own. **The commit it is given is the worker's claim, not the commit to look at**: the source reads
/// the head of the mission's branch from the forge itself and says it in <c>HeadCommit</c>; a claim that differs, or a head it cannot read, fails the check.
/// **It is called while the lock of the problem is held, so it must not block**: it answers from what it has fetched, with a time limit of its own, and never from a call that can hang (a slow forge must not hold the stop and the sweep).
/// </summary>
public interface IFactsSource
{
    ObservedFacts Facts(FactsQuery query);
}

/// <summary>
/// What a source of facts is asked (for an act: **the repository, the commit and the base it is about**, which the source reads and reports back): the problem and the mission (none yet at the start of a problem), the world, the **frame of the mission** (its repositories, so that the source reads the right one), the dossier and the commit the worker claims.
/// </summary>
public sealed record FactsQuery(string ProblemId, string? MissionId, World World, MissionFrame Frame, Dossier? Dossier, string? Commit, string? Repository = null, string? BaseCommit = null);

/// <summary>Which runner serves a world for an application (a runner profile on a host).</summary>
public interface IRunnerDirectory
{
    string RunnerFor(World world, string applicationId);
}
