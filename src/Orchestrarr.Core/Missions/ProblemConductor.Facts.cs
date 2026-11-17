namespace Orchestrarr.Core.Missions;

/// <summary>The checks of the code on the facts the control plane fetched itself (FR-MAN-7), at a crossing and at an act.</summary>
public sealed partial class ProblemConductor
{
    // The checks to close a problem in the world of the mission: the facts of that world on the commit the worker claims, with no new mandate to issue
    private List<PreconditionFailure> ClosingChecks(ProblemRun run, Mission mission, Dossier? dossier) =>
        CheckFacts(run, mission.World, mission.Frame, mission.Id, previousDossierExists: dossier is not null, dossier, commit: dossier?.Commit, issuing: false);

    // The facts come from the control plane's own source; the plan, the stop, the loop and the staging are the conductor's own: none is taken from the worker
    // `issuing` is true only where a new mandate is about to be issued: the bound of the loop guards a new mandate, not the acts of one that is already in force nor the closing of the problem
    private List<PreconditionFailure> CheckFacts(ProblemRun run, World world, MissionFrame frame, string? missionId, bool previousDossierExists, Dossier? dossier, string? commit, bool issuing, string? repository = null, string? baseCommit = null)
    {
        var observed = facts.Facts(new FactsQuery(run.ProblemId, missionId, world, frame, dossier, commit, repository, baseCommit));
        // The commit the facts are about is the head that the source read itself; the worker's pointer is only a claim, and a claim that does not match (or that cannot be matched) fails the check
        // Past the staging world, no commit named is no claim that can match: unknown counts as failing
        var claimMatches = observed.WorkerClaimMatchesFacts
            && (commit is null ? world == World.Staging : observed.HeadCommit is not null && string.Equals(observed.HeadCommit, commit, StringComparison.Ordinal))
            // For an act, the source read the repository and the base that the act names, and says so: nothing else is the commit that was checked
            && (repository is null || string.Equals(observed.Repository, repository, StringComparison.Ordinal))
            && (baseCommit is null || string.Equals(observed.BaseCommit, baseCommit, StringComparison.Ordinal));
        // The diff is clean when the source says so **and** the paths it changed are known and none is forbidden by the frame of this mission (the options' list and the certificate's floor)
        var diffClean = observed.DiffIsClean == true && observed.ChangedPaths is not null && !PathPatterns.AnyMatches(frame.ForbiddenPaths, observed.ChangedPaths);
        var loop = issuing ? options.Loop.Check(mandates.CountForRun(run.ProblemId, run.Approval.Certificate.Id), time.GetUtcNow() - run.StartedAt) : LoopVerdict.Allowed;
        // Every fact is named: a field cannot take the place of another by its position
        var checks = new PreconditionFacts(
            World: world,
            PlanCovers: true,
            BudgetAvailable: observed.BudgetAvailable,
            KillSwitchEngaged: stop.Engaged,
            // The verdict of the staging is the one that Start decided on the state it was given; reading the state again at each crossing needs the source of the staging runner, which is not built (FR-STG-4, R-58-183)
            Staging: run.UsesStaging ? StagingVerdict.Usable : StagingVerdict.UsableWithNoNet,
            PreviousDossierExists: previousDossierExists,
            DiffIsClean: diffClean,
            CiGreenOnExactCommit: observed.CiGreenOnExactCommit,
            ArtifactIsTheOneProvedInStaging: observed.ArtifactIsTheOneProvedInStaging,
            WorkerClaimMatchesFacts: claimMatches,
            Loop: loop,
            WayBackKnown: observed.WayBackKnown,
            MergeMayDeploy: observed.MergeMayDeploy,
            ExecutedFilesTouched: observed.ExecutedFilesTouched,
            TagCreationBlocked: observed.TagCreationBlocked,
            BranchCannotReachWriteCredentials: observed.BranchCannotReachWriteCredentials,
            IdentityConfinedToItsBranches: observed.IdentityConfinedToItsBranches);
        // The release of an application with a staging needs the artifact that was proved there (FR-WLD-14)
        return [.. MandatePreconditions.Check(checks, requiresStagingProof: run.UsesStaging && world == World.Release)];
    }
}
