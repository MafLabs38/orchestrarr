using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

/// <summary>The control plane justifies the permission to change a pipeline (ADR 0057): the operation must be about its pipeline **and** its approved frame must leave the pipeline files open.</summary>
public sealed class PushGrantsTests
{
    private static PlanEntry Entry(string kind, string? nature = null, int? pullRequest = null) =>
        new("p", "app", "x", "x", ["o/r"], World.Git, new MissionBudget(30, 3, 50), new Validation("alice", null), DateTimeOffset.UnixEpoch, pullRequest,
            Brief: new ProblemBrief(kind, "o/r", "d", true, Nature: nature));

    private static MissionFrame Frame(PlanEntry entry) => new(["o/r"], [.. ForbiddenPathsDefaults.Floor, .. PipelineChanges.ForbiddenFor(entry)], "here");

    [Theory]
    [InlineData("FailingCi", null, true)]
    [InlineData("DependencyUpdate", "PipelineToolUpdate", true)]
    [InlineData("ImageUpdate", "BaseImageUpdate", true)]
    [InlineData("DependencyAlert", "SecurityAlert", false)]
    [InlineData("DependencyUpdate", "LibraryUpdate", false)]
    public void The_credential_may_change_a_workflow_only_for_an_operation_about_its_pipeline(string kind, string? nature, bool pipelines)
    {
        var entry = Entry(kind, nature);

        var grant = PushGrants.For(entry, Frame(entry));

        grant.Pipelines.Should().Be(pipelines);
        grant.Justification.Should().Contain(pipelines ? "about its pipeline" : "not about its pipeline");
    }

    [Fact]
    public void A_frame_that_forbids_the_pipelines_wins_over_what_the_row_says()
    {
        var entry = Entry("FailingCi");

        var grant = PushGrants.For(entry, new MissionFrame(["o/r"], [".github/**"], "here"));

        grant.Pipelines.Should().BeFalse("the frame is what the maintainer's approval covers: the control plane cannot give more");
        grant.Justification.Should().Contain("frame");
    }

    [Theory]
    [InlineData(".github/workflows/release.yml")]
    [InlineData("**/.github/workflows/**")]
    [InlineData(".github/workflows/*.yaml")]
    [InlineData(".github/workflows/build-*.yml")]
    [InlineData(".github/*/ci.yml")]
    [InlineData(".github/work*/**")]
    [InlineData("**/*.yml")]
    public void A_frame_that_forbids_any_workflow_gets_no_permission_on_the_workflows_since_the_forge_grants_them_all(string forbidden) =>
        PushGrants.For(Entry("FailingCi"), new MissionFrame(["o/r"], [.. ForbiddenPathsDefaults.Floor, forbidden], "here")).Pipelines.Should().BeFalse();

    [Fact]
    public void The_floor_of_every_frame_does_not_by_itself_take_the_workflows_away() =>
        PushGrants.For(Entry("FailingCi"), Frame(Entry("FailingCi"))).Pipelines.Should().BeTrue("the floor names other files of the forge's folder, not its workflows");

    [Fact]
    public void Only_the_staging_of_a_problem_with_no_pull_request_lists_the_push_of_a_branch()
    {
        WorldActs.Of(World.Staging, Entry("FailingCi")).Should().Equal(GatedAct.PushBranch);
        WorldActs.Of(World.Staging, Entry("DependencyUpdate", pullRequest: 7)).Should().BeEmpty("the staging of a pull request pushes nothing: the gateway carries a repair");
        WorldActs.Of(World.Git, Entry("FailingCi")).Should().NotContain(GatedAct.PushBranch);
        WorldActs.Of(World.Release, Entry("FailingCi")).Should().NotContain(GatedAct.PushBranch);
    }
}
