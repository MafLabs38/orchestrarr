namespace Orchestrarr.Core.Missions;

/// <summary>
/// The facts the control plane **fetched itself**, never taken from the runner or from the dossier (FR-MAN-7): the diff and the CI status from the forge on the exact
/// commit, the health from Portarr. A mismatch between the worker's claim and these facts fails a check before the controller runs.
/// </summary>
public sealed record PreconditionFacts(
    World World,
    bool PlanCovers,
    bool BudgetAvailable,
    bool KillSwitchEngaged,
    StagingVerdict Staging,
    bool PreviousDossierExists,
    bool? DiffIsClean,
    bool? CiGreenOnExactCommit,
    bool? ArtifactIsTheOneProvedInStaging,
    bool WorkerClaimMatchesFacts,
    LoopVerdict Loop,
    bool? WayBackKnown = null,
    bool? MergeMayDeploy = null,
    bool? ExecutedFilesTouched = null,
    bool? TagCreationBlocked = null,
    bool? BranchCannotReachWriteCredentials = null,
    bool? IdentityConfinedToItsBranches = null);

public enum PreconditionFailure
{
    PlanDoesNotCover,
    BudgetUnavailable,
    KillSwitchEngaged,
    StagingNotUsable,
    NoPreviousDossier,
    DiffTouchesForbiddenPath,
    CiNotGreenOnExactCommit,
    ArtifactNotProvedInStaging,
    ClaimDoesNotMatchFacts,
    LoopLimitReached,

    /// <summary>The previous version is not known, or cannot be restored through the process (FR-WLD-13, D-93): it does not mean that an automatic rollback exists, there is none (ADR 0040). Unknown counts as failing.</summary>
    NoWayBackKnown,

    /// <summary>The merge of the Git world may deploy (or is not known not to): a merge that deploys is a release-world act, never performed under a Git-world mandate whatever the approval reaches; it goes through the release checks (the way back, the proof of the staging, the files the pipeline executes).</summary>
    MergeMayDeployUnderGitMandate,

    /// <summary>The merge may deploy and the change touches files that the pipeline executes: the code that reaches the deployment secrets was not proved, so a person decides (the conductor escalates).</summary>
    ExecutedFilesTouchedWhereMergeDeploys,

    /// <summary>The forge does not block the agent's identity from creating tags and releases (a ruleset), or it is not known: a tag could deploy without the release-world mandate.</summary>
    AgentCouldCreateTags,

    /// <summary>The certificate of the approval already backed as many mandates as it says: no mandate can be issued under it (this is not the loop bound of the problem).</summary>
    ApprovalBacksNoMoreMandates,

    /// <summary>The agent's identity on the forge can write to branches other than its own prefix (no ruleset confines it), or it is not known: a push to a human's or a bot's branch could be merged with no mandate. Not known counts as unconfined.</summary>
    AgentIdentityNotConfined,

    /// <summary>
    /// A job that runs on the agent's branch could obtain a **write credential**, not only a secret: a workflow of the base branch's context (<c>pull_request_target</c>, <c>workflow_run</c>) that runs what the agent pushed, a secret outside an environment that needs
    /// a reviewer, an <c>id-token: write</c> permission on a workflow that the agent's branch triggers (or a federation trust not pinned to protected refs or environments), a write scope of the default workflow token, or the setting that lets Actions
    /// approve pull requests. Not known counts as reachable.
    /// </summary>
    AgentBranchCouldReachWriteCredentials,

    /// <summary>The pipeline's result for the exact commit is not known (still running, or the source could not read it): added to <see cref="CiNotGreenOnExactCommit"/>, so that a result that is not there is told apart from a pipeline that failed.</summary>
    CiResultNotKnown,

    /// <summary>In the release world: the commit that the release names is not the one that the controller accepted at the crossing into it. Added by the conductor, which holds both, not by the source of the facts.</summary>
    ReleaseCommitIsNotTheAcceptedOne,
}

/// <summary>What the code checks and never leaves to a judgement (FR-MAN-7). The controller judges the rest. <c>DiffTouchesForbiddenPath</c> also covers a diff that is not known: unknown counts as failing.</summary>
public static class MandatePreconditions
{
    /// <param name="facts">The facts.</param>
    /// <param name="requiresStagingProof">Whether the application has a staging: a release then needs the artifact that was proved there (FR-WLD-14).</param>
    public static IReadOnlyList<PreconditionFailure> Check(PreconditionFacts facts, bool requiresStagingProof)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var failures = new List<PreconditionFailure>();
        Add(failures, !facts.PlanCovers, PreconditionFailure.PlanDoesNotCover);
        Add(failures, !facts.BudgetAvailable, PreconditionFailure.BudgetUnavailable);
        Add(failures, facts.KillSwitchEngaged, PreconditionFailure.KillSwitchEngaged);
        // The staging is a condition to start the job and to work in the staging world, where the problem holds it: it is released when the change leaves for the Git world (the pull request is
        // opened), so the later worlds do not ask for it (they ask for the proof that was made there, below)
        Add(failures, facts.World == World.Staging && facts.Staging is not (StagingVerdict.Usable or StagingVerdict.UsableWithNoNet), PreconditionFailure.StagingNotUsable);
        Add(failures, facts.Loop != LoopVerdict.Allowed, PreconditionFailure.LoopLimitReached);
        // The first mission of a problem has no dossier before it; the next worlds do
        Add(failures, facts.World != World.Staging && !facts.PreviousDossierExists, PreconditionFailure.NoPreviousDossier);
        // A diff that is not known to be clean is not clean: unknown counts as failing
        Add(failures, facts.DiffIsClean != true, PreconditionFailure.DiffTouchesForbiddenPath);
        Add(failures, facts.World != World.Staging && facts.CiGreenOnExactCommit != true, PreconditionFailure.CiNotGreenOnExactCommit);
        Add(failures, facts.World != World.Staging && facts.CiGreenOnExactCommit is null, PreconditionFailure.CiResultNotKnown);
        Add(failures, facts.World == World.Release && requiresStagingProof && facts.ArtifactIsTheOneProvedInStaging != true, PreconditionFailure.ArtifactNotProvedInStaging);
        Add(failures, !facts.WorkerClaimMatchesFacts, PreconditionFailure.ClaimDoesNotMatchFacts);
        // A merge that may deploy is a release act (ADR 0035 point 7): refused under an approval that stops before the release world, unknown counts as deploying
        var mayDeploy = facts.MergeMayDeploy != false;
        Add(failures, facts.World == World.Git && mayDeploy, PreconditionFailure.MergeMayDeployUnderGitMandate);
        // In the release world, where that merge belongs, a change that touches what the pipeline executes is for a person to decide
        Add(failures, facts.World == World.Release && mayDeploy && facts.ExecutedFilesTouched != false, PreconditionFailure.ExecutedFilesTouchedWhereMergeDeploys);
        // A tag or a release can deploy as a merge can: the forge must stop the agent's identity from creating one, as a fact read from the repository's rulesets
        Add(failures, facts.World == World.Git && facts.TagCreationBlocked != true, PreconditionFailure.AgentCouldCreateTags);
        // What the agent pushes must not run with a write credential of any kind (see the failure): read from the repository and the forge, unknown counts as reachable
        Add(failures, facts.World == World.Git && facts.BranchCannotReachWriteCredentials != true, PreconditionFailure.AgentBranchCouldReachWriteCredentials);
        // The agent writes only to its own prefix, by a ruleset of the forge: read from the repository, unknown counts as unconfined
        Add(failures, facts.World == World.Git && facts.IdentityConfinedToItsBranches != true, PreconditionFailure.AgentIdentityNotConfined);
        // A deployment needs a way back known before it starts (FR-WLD-13, D-93): the previous version is known and can be restored through the process; unknown counts as failing
        Add(failures, facts.World == World.Release && facts.WayBackKnown != true, PreconditionFailure.NoWayBackKnown);
        return failures;
    }

    private static void Add(List<PreconditionFailure> failures, bool failed, PreconditionFailure failure)
    {
        if (failed)
        {
            failures.Add(failure);
        }
    }
}
