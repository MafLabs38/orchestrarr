using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

public sealed class PreconditionsTests
{
    private static PreconditionFacts Good(World world) => new(world, PlanCovers: true, BudgetAvailable: true, KillSwitchEngaged: false, Staging: StagingVerdict.Usable,
        PreviousDossierExists: true, DiffIsClean: true, CiGreenOnExactCommit: true, ArtifactIsTheOneProvedInStaging: true, WorkerClaimMatchesFacts: true, Loop: LoopVerdict.Allowed, WayBackKnown: true, MergeMayDeploy: false, ExecutedFilesTouched: false, TagCreationBlocked: true, BranchCannotReachWriteCredentials: true, IdentityConfinedToItsBranches: true);

    [Theory]
    [InlineData(World.Staging)]
    [InlineData(World.Git)]
    [InlineData(World.Release)]
    public void Every_fact_in_order_lets_a_mandate_through(World world)
    {
        MandatePreconditions.Check(Good(world), requiresStagingProof: true).Should().BeEmpty();
    }

    [Theory]
    [InlineData(World.Staging, true)]
    [InlineData(World.Git, false)]
    [InlineData(World.Release, false)]
    public void The_staging_is_asked_for_by_the_staging_world_only_because_the_problem_releases_it_when_the_change_leaves(World world, bool refused)
    {
        var failures = MandatePreconditions.Check(Good(world) with { Staging = StagingVerdict.RefusedBrokenOrStale }, requiresStagingProof: true);

        failures.Contains(PreconditionFailure.StagingNotUsable).Should().Be(refused);
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
            WayBackKnown = null,
        };

        MandatePreconditions.Check(facts, requiresStagingProof: true).Should().BeEquivalentTo(Enum.GetValues<PreconditionFailure>().Where(failure => failure is not (PreconditionFailure.MergeMayDeployUnderGitMandate or PreconditionFailure.ExecutedFilesTouchedWhereMergeDeploys or PreconditionFailure.AgentCouldCreateTags or PreconditionFailure.AgentBranchCouldReachWriteCredentials or PreconditionFailure.AgentIdentityNotConfined or PreconditionFailure.ApprovalBacksNoMoreMandates or PreconditionFailure.StagingNotUsable or PreconditionFailure.CiResultNotKnown or PreconditionFailure.ReleaseCommitIsNotTheAcceptedOne or PreconditionFailure.IntegrationBranchRedAfterMerge)), "the checks about a merge are for the Git world, the staging is held by the staging world only, and the count of a certificate and a red branch after a merge are the conductor's");
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

        MandatePreconditions.Check(unknown, requiresStagingProof: true).Should().Equal(PreconditionFailure.CiNotGreenOnExactCommit, PreconditionFailure.CiResultNotKnown);
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

    [Fact]
    public void A_merge_that_may_deploy_is_never_a_git_world_act_whatever_the_approval_and_unknown_counts_as_deploying()
    {
        var git = Good(World.Git);

        MandatePreconditions.Check(git with { MergeMayDeploy = true }, false).Should().Equal(PreconditionFailure.MergeMayDeployUnderGitMandate);
        MandatePreconditions.Check(git with { MergeMayDeploy = null }, false).Should().Equal(PreconditionFailure.MergeMayDeployUnderGitMandate);
        MandatePreconditions.Check(git with { MergeMayDeploy = false }, false).Should().BeEmpty("a merge that does not deploy is a Git act");
    }

    [Fact]
    public void In_the_release_world_a_merge_that_deploys_with_executed_files_touched_or_unknown_is_for_a_person()
    {
        var release = Good(World.Release) with { MergeMayDeploy = true };

        MandatePreconditions.Check(release with { ExecutedFilesTouched = true }, false).Should().Equal(PreconditionFailure.ExecutedFilesTouchedWhereMergeDeploys);
        MandatePreconditions.Check(release with { ExecutedFilesTouched = null }, false).Should().Equal(PreconditionFailure.ExecutedFilesTouchedWhereMergeDeploys);
        MandatePreconditions.Check(release with { ExecutedFilesTouched = false }, false).Should().BeEmpty();
        MandatePreconditions.Check(Good(World.Release) with { MergeMayDeploy = false, ExecutedFilesTouched = true }, false).Should().BeEmpty("a deployment that is not a merge has no executed files to fear");
    }

    [Fact]
    public void An_agent_identity_that_could_create_a_tag_or_a_release_is_not_admitted_to_the_git_world_and_unknown_counts_as_able()
    {
        MandatePreconditions.Check(Good(World.Git) with { TagCreationBlocked = null }, false).Should().Equal(PreconditionFailure.AgentCouldCreateTags);
        MandatePreconditions.Check(Good(World.Git) with { TagCreationBlocked = false }, false).Should().Equal(PreconditionFailure.AgentCouldCreateTags);
    }

    [Fact]
    public void A_branch_whose_code_could_run_with_a_secret_is_not_admitted_to_the_git_world_and_unknown_counts_as_reachable()
    {
        MandatePreconditions.Check(Good(World.Git) with { BranchCannotReachWriteCredentials = null }, false).Should().Equal(PreconditionFailure.AgentBranchCouldReachWriteCredentials);
        MandatePreconditions.Check(Good(World.Git) with { BranchCannotReachWriteCredentials = false }, false).Should().Equal(PreconditionFailure.AgentBranchCouldReachWriteCredentials);
        MandatePreconditions.Check(Good(World.Staging) with { BranchCannotReachWriteCredentials = null }, false).Should().BeEmpty("the check is for the Git world");
    }

    [Fact]
    public void An_identity_that_can_write_to_other_branches_than_its_own_is_not_admitted_to_the_git_world_and_unknown_counts_as_unconfined()
    {
        MandatePreconditions.Check(Good(World.Git) with { IdentityConfinedToItsBranches = null }, false).Should().Equal(PreconditionFailure.AgentIdentityNotConfined);
        MandatePreconditions.Check(Good(World.Git) with { IdentityConfinedToItsBranches = false }, false).Should().Equal(PreconditionFailure.AgentIdentityNotConfined);
        MandatePreconditions.Check(Good(World.Release) with { IdentityConfinedToItsBranches = null }, false).Should().BeEmpty("the check is for the Git world");
    }

}
