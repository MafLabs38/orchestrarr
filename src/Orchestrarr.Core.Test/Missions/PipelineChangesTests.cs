using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

/// <summary>Only an operation about the pipeline may touch it, and nothing ever weakens it (ADR 0055): read by the frame of the missions, by the policy of the free agent's changes and by the policy of the repairs.</summary>
public sealed class PipelineChangesTests
{
    private static PlanEntry Entry(string kind, string? nature = null) =>
        new("p", "app", "x", "x", ["o/r"], World.Release, new MissionBudget(30, 3, 50), new Validation("alice", null), DateTimeOffset.UnixEpoch,
            Brief: new ProblemBrief(kind, "o/r", "d", true, Nature: nature));

    [Theory]
    [InlineData("FailingCi", null, true)]
    [InlineData("DependencyUpdate", "PipelineToolUpdate", true)]
    [InlineData("ImageUpdate", "BaseImageUpdate", true)]
    [InlineData("DependencyUpdate", "LibraryUpdate", false)]
    [InlineData("DependencyAlert", "SecurityAlert", false)]
    [InlineData("ImageUpdate", "ImageBehind", false)]
    public void Only_an_operation_about_the_pipeline_has_the_pipelines_out_of_its_frame(string kind, string? nature, bool may)
    {
        PipelineChanges.MayChange(Entry(kind, nature)).Should().Be(may);
        PipelineChanges.ForbiddenFor(Entry(kind, nature)).Should().HaveCount(may ? 0 : PipelineChanges.Paths.Count);
    }

    [Fact]
    public void An_operation_with_no_brief_may_not_touch_the_pipelines() =>
        PipelineChanges.MayChange(Entry("FailingCi") with { Brief = null }).Should().BeFalse();

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData("services/api/.github/workflows/ci.yml")]
    [InlineData(".gitlab-ci.yml")]
    [InlineData(".forgejo/workflows/build.yaml")]
    [InlineData(".woodpecker/test.yml")]
    [InlineData("Jenkinsfile")]
    [InlineData("azure-pipelines.yml")]
    public void The_pipeline_files_of_every_forge_are_known(string path) =>
        PipelineChanges.IsPipeline(path).Should().BeTrue();

    [Theory]
    [InlineData("        continue-on-error: true")]
    [InlineData("    if: false")]
    [InlineData("      - run: npm test || true")]
    [InlineData("      - run: exit 0")]
    [InlineData("  pull_request_target:")]
    [InlineData("  workflow_run:")]
    [InlineData("permissions: write-all")]
    [InlineData("  contents: write")]
    [InlineData("          TOKEN: ${{ secrets.DEPLOY_TOKEN }}")]
    [InlineData("  allow_failure: true")]
    public void A_line_that_weakens_a_pipeline_is_refused(string line) =>
        PipelineChanges.Weakening(".github/workflows/ci.yml", [line]).Should().NotBeNull().And.Contain("weakens the pipeline");

    [Theory]
    [InlineData("        node-version: 22")]
    [InlineData("      - uses: actions/setup-node@v5")]
    [InlineData("      - run: npm ci && npm test")]
    [InlineData("  contents: read")]
    [InlineData("    if: github.event_name == 'push'")]
    public void A_line_that_maintains_a_pipeline_passes(string line) =>
        PipelineChanges.Weakening(".github/workflows/ci.yml", [line]).Should().BeNull();

    [Fact]
    public void A_change_of_the_free_agent_is_read_on_the_lines_it_adds_to_a_pipeline()
    {
        static BranchChange Change(string patch) => new("c0ffee", 0, 1, [new BranchFile(".github/workflows/ci.yml", "modified", 2, patch)], FilesComplete: true);

        ChangePolicy.Refusal(Change("@@ -5,1 +5,1 @@\n-      - run: npm test\n+      - run: npm test || true")).Should().Contain("weakens the pipeline");
        ChangePolicy.Refusal(Change("@@ -5,1 +5,1 @@\n-        continue-on-error: true\n+        node-version: 22")).Should().BeNull("removing a weakening is not one");
    }

    [Fact]
    public void A_change_that_touches_a_pipeline_outside_its_frame_is_refused_even_when_it_does_not_weaken_it()
    {
        var change = new BranchChange("c0ffee", 0, 1, [new BranchFile(".github/workflows/ci.yml", "modified", 2, "@@ -5,1 +5,1 @@\n-        node-version: 20\n+        node-version: 22")], FilesComplete: true);

        ChangePolicy.Refusal(change, PipelineChanges.ForbiddenFor(Entry("DependencyUpdate", "LibraryUpdate"))).Should().Contain("forbidden by the frame");
        ChangePolicy.Refusal(change, PipelineChanges.ForbiddenFor(Entry("FailingCi"))).Should().BeNull();
    }

    [Fact]
    public void A_repair_is_read_on_the_whole_pipeline_file_it_carries() =>
        RepairPolicy.Refusal([new PatchedFile(".github/workflows/ci.yml", "on: push\njobs:\n  test:\n    runs-on: ubuntu-latest\n    continue-on-error: true\n")]).Should().Contain("weakens the pipeline");
}
