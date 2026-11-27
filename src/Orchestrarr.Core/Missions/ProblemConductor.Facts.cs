namespace Orchestrarr.Core.Missions;

/// <summary>The checks of the code on the facts the control plane fetched itself (FR-MAN-7), at a crossing and at an act.</summary>
public sealed partial class ProblemConductor
{
    // The checks to close a problem in the world of the mission: the facts of that world on the commit the worker claims, with no new mandate to issue
    private async Task<List<PreconditionFailure>> ClosingChecksAsync(ProblemRun run, Mission mission, Dossier? dossier, CancellationToken cancellationToken) =>
        await CheckFactsAsync(run, new FactsCheck(mission.World, mission.Frame, mission.Id, dossier, HasPreviousDossier: dossier is not null, Commit: dossier?.Commit, Issuing: false,
            new FactsScope(CompareAccepted: mission.World == World.Release, AcceptedCommit: mission.World == World.Release ? LatestDossier(run, mission)?.Commit : null)), cancellationToken);

    // The facts come from the control plane's own source; the plan, the stop, the loop and the staging are the conductor's own: none is taken from the worker
    // `issuing` is true only where a new mandate is about to be issued: the bound of the loop guards a new mandate, not the acts of one that is already in force nor the closing of the problem
    private async Task<List<PreconditionFailure>> CheckFactsAsync(ProblemRun run, FactsCheck check, CancellationToken cancellationToken)
    {
        var observed = await facts.FactsAsync(new FactsQuery(run.ProblemId, check.MissionId, check.InWorld, check.Frame, check.HandedOver, check.Commit, check.Scope.Repository, check.Scope.BaseCommit), cancellationToken);
        // The check.Commit the facts are about is the head that the source read itself; the worker's pointer is only a claim, and a claim that does not match (or that cannot be matched) fails the check
        // Past the staging check.InWorld, no check.Commit named is no claim that can match: unknown counts as failing
        var claimMatches = observed.WorkerClaimMatchesFacts
            && (check.Commit is null ? check.InWorld == World.Staging : observed.HeadCommit is not null && string.Equals(observed.HeadCommit, check.Commit, StringComparison.Ordinal))
            // For an act, the source read the repository and the base that the act names, and says so: nothing else is the check.Commit that was checked
            && (check.Scope.Repository is null || string.Equals(observed.Repository, check.Scope.Repository, StringComparison.Ordinal))
            && (check.Scope.BaseCommit is null || string.Equals(observed.BaseCommit, check.Scope.BaseCommit, StringComparison.Ordinal));
        // The diff is clean when the source says so **and** the paths it changed are known and none is forbidden by the check.Frame of this mission (the options' list and the certificate's floor)
        var diffClean = observed.DiffIsClean == true && observed.ChangedPaths is not null && !PathPatterns.AnyMatches(check.Frame.ForbiddenPaths, observed.ChangedPaths);
        var loop = check.Issuing ? options.Loop.Check(mandates.CountForRun(run.ProblemId, run.Approval.Certificate.Id), time.GetUtcNow() - run.StartedAt) : LoopVerdict.Allowed;
        // Every fact is named: a field cannot take the place of another by its position
        var checks = new PreconditionFacts(
            World: check.InWorld,
            PlanCovers: true,
            BudgetAvailable: observed.BudgetAvailable,
            KillSwitchEngaged: stop.Engaged,
            // The verdict of the staging is the one that Start decided on the state it was given: the problem holds that staging while it works in the staging check.InWorld, and releases it when the change leaves for
            // the Git check.InWorld, so only the staging check.InWorld asks for it (FR-STG-4); the later worlds ask for the proof made there instead
            Staging: run.UsesStaging ? StagingVerdict.Usable : StagingVerdict.UsableWithNoNet,
            PreviousDossierExists: check.HasPreviousDossier,
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
        var failures = MandatePreconditions.Check(checks, requiresStagingProof: run.UsesStaging && check.InWorld == World.Release).ToList();
        // In the release check.InWorld the check.Commit that the source read is the check.Commit that the controller accepted at the crossing into it, the one the act gateway deploys: the comparison is on what the source read,
        // never on the worker's claim; a release that is not that check.Commit is an anomaly of the delivery
        if (check.Scope.CompareAccepted && (check.Scope.AcceptedCommit is null || !string.Equals(observed.HeadCommit, check.Scope.AcceptedCommit, StringComparison.Ordinal)))
        {
            failures.Add(PreconditionFailure.ReleaseCommitIsNotTheAcceptedOne);
        }
        // A merge that turned the integration branch red: read in the Git world, and at the crossing out of it (the only crossing into the release world that is issued). A branch that was red before the merge was not turned
        // red by it: the red pipeline stays what it is, and no fix forward is made for a fault that is not the change's. A base that was not read green is not blamed on the merge either: a fix forward starts an agent, and an
        // unknown fact never leads to an act (a person decides)
        if (observed.Merged == true && observed.CiGreenOnExactCommit == false && (check.InWorld == World.Git || (check.Issuing && check.InWorld == World.Release)))
        {
            failures.Add(observed.BaseWasRedBeforeMerge == false ? PreconditionFailure.IntegrationBranchRedAfterMerge : PreconditionFailure.IntegrationBranchRedBeforeMerge);
        }
        return failures;
    }

    /// <summary>One check of the facts: the world and the frame it is about, the mission and its dossier, the commit the worker claims, whether a new mandate is about to be issued, and its scope. Built by name at each call site.</summary>
    private sealed record FactsCheck(World InWorld, MissionFrame Frame, string? MissionId, Dossier? HandedOver, bool HasPreviousDossier, string? Commit, bool Issuing, FactsScope Scope);

    /// <summary>What a check of the facts is scoped to beyond the mission: for an act, the repository and the base it is about; in the release world, the commit that was accepted and that the one read must be (no accepted commit counts as failing).</summary>
    private sealed record FactsScope(string? Repository = null, string? BaseCommit = null, string? AcceptedCommit = null, bool CompareAccepted = false);
}
