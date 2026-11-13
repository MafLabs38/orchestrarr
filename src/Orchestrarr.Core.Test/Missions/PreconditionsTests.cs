using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

public sealed class PreconditionsTests
{
    private static PreconditionFacts Good(World world) => new(world, PlanCovers: true, BudgetAvailable: true, KillSwitchEngaged: false, Staging: StagingVerdict.Usable,
        PreviousDossierExists: true, DiffIsClean: true, CiGreenOnExactCommit: true, ArtifactIsTheOneProvedInStaging: true, WorkerClaimMatchesFacts: true, Loop: LoopVerdict.Allowed);

    [Theory]
    [InlineData(World.Staging)]
    [InlineData(World.Git)]
    [InlineData(World.Release)]
    public void Every_fact_in_order_lets_a_mandate_through(World world)
    {
        MandatePreconditions.Check(Good(world), requiresStagingProof: true).Should().BeEmpty();
    }

    [Fact]
    public void Each_check_of_the_code_names_what_failed()
    {
        var facts = Good(World.Release) with
        {
            PlanCovers = false,
            BudgetAvailable = false,
            KillSwitchEngaged = true,
            Staging = StagingVerdict.RefusedNotProven,
            PreviousDossierExists = false,
            DiffIsClean = false,
            CiGreenOnExactCommit = false,
            ArtifactIsTheOneProvedInStaging = false,
            WorkerClaimMatchesFacts = false,
            Loop = LoopVerdict.TooLong,
        };

        MandatePreconditions.Check(facts, requiresStagingProof: true).Should().BeEquivalentTo(Enum.GetValues<PreconditionFailure>());
    }

    [Fact]
    public void A_diff_that_is_not_known_to_be_clean_fails_the_check_unknown_counts_as_failing()
    {
        MandatePreconditions.Check(Good(World.Git) with { DiffIsClean = null }, requiresStagingProof: false).Should().Equal(PreconditionFailure.DiffTouchesForbiddenPath);
    }

    [Fact]
    public void The_first_mission_of_a_problem_needs_no_previous_dossier_and_no_green_pipeline_yet()
    {
        var facts = Good(World.Staging) with { PreviousDossierExists = false, CiGreenOnExactCommit = null };

        MandatePreconditions.Check(facts, requiresStagingProof: true).Should().BeEmpty();
    }

    [Fact]
    public void A_release_needs_the_green_pipeline_of_the_exact_commit_not_an_unknown_one()
    {
        var unknown = Good(World.Release) with { CiGreenOnExactCommit = null };

        MandatePreconditions.Check(unknown, requiresStagingProof: true).Should().Equal(PreconditionFailure.CiNotGreenOnExactCommit);
    }

    [Fact]
    public void A_release_of_an_application_with_a_staging_needs_the_artifact_that_was_proved_there_and_one_without_does_not()
    {
        var other = Good(World.Release) with { ArtifactIsTheOneProvedInStaging = false };

        MandatePreconditions.Check(other, requiresStagingProof: true).Should().Equal(PreconditionFailure.ArtifactNotProvedInStaging);
        MandatePreconditions.Check(other, requiresStagingProof: false).Should().BeEmpty();
    }

    [Fact]
    public void A_worker_that_claims_what_the_facts_do_not_show_fails_before_the_controller_runs()
    {
        MandatePreconditions.Check(Good(World.Git) with { WorkerClaimMatchesFacts = false }, requiresStagingProof: false).Should().Equal(PreconditionFailure.ClaimDoesNotMatchFacts);
    }
}
