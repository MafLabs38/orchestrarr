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
    LoopVerdict Loop);

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
        Add(failures, facts.Staging is not (StagingVerdict.Usable or StagingVerdict.UsableWithNoNet), PreconditionFailure.StagingNotUsable);
        Add(failures, facts.Loop != LoopVerdict.Allowed, PreconditionFailure.LoopLimitReached);
        // The first mission of a problem has no dossier before it; the next worlds do
        Add(failures, facts.World != World.Staging && !facts.PreviousDossierExists, PreconditionFailure.NoPreviousDossier);
        // A diff that is not known to be clean is not clean: unknown counts as failing
        Add(failures, facts.DiffIsClean != true, PreconditionFailure.DiffTouchesForbiddenPath);
        Add(failures, facts.World != World.Staging && facts.CiGreenOnExactCommit != true, PreconditionFailure.CiNotGreenOnExactCommit);
        Add(failures, facts.World == World.Release && requiresStagingProof && facts.ArtifactIsTheOneProvedInStaging != true, PreconditionFailure.ArtifactNotProvedInStaging);
        Add(failures, !facts.WorkerClaimMatchesFacts, PreconditionFailure.ClaimDoesNotMatchFacts);
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
