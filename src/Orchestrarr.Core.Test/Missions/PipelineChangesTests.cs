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
    [InlineData("        continue-on-error: ${{ true }}")]
    [InlineData("    if: ${{ false }}")]
    [InlineData("  contents: 'write' # needed")]
    [InlineData("    permissions: { contents: write }")]
    [InlineData("    secrets: inherit")]
    [InlineData("          ALL: ${{ toJSON(secrets) }}")]
    [InlineData("          X: ${{ secrets['DEPLOY'] }}")]
    [InlineData("          set +e")]
    [InlineData("  issue_comment:")]
    public void A_line_that_weakens_a_pipeline_is_refused(string line) =>
        PipelineChanges.Weakening(".github/workflows/ci.yml", [line]).Should().NotBeNull().And.Contain("weakens the pipeline");

    [Theory]
    [InlineData("        node-version: 22")]
    [InlineData("      - uses: actions/setup-node@v5")]
    [InlineData("      - run: npm ci && npm test")]
    [InlineData("  contents: read")]
    [InlineData("    if: github.event_name == 'push'")]
    [InlineData("        continue-on-error: false")]
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

    [Fact]
    public void A_change_that_takes_a_step_out_of_a_pipeline_is_refused_and_one_that_replaces_a_step_is_not()
    {
        static BranchChange Change(string patch) => new("c0ffee", 0, 1, [new BranchFile(".github/workflows/ci.yml", "modified", 2, patch)], FilesComplete: true);

        ChangePolicy.Refusal(Change("@@ -5,2 +5,1 @@\n       - run: npm ci\n-      - run: npm test")).Should().Contain("removes 1 step");
        ChangePolicy.Refusal(Change("@@ -5,1 +5,1 @@\n-      - run: npm test\n+      - run: npm test -- --seed 1")).Should().BeNull();
    }

    [Fact]
    public void A_repair_is_judged_on_what_it_adds_to_the_pipeline_file_of_the_repaired_head()
    {
        const string Head = "on: push\njobs:\n  test:\n    steps:\n      - run: npm test\n        env:\n          TOKEN: ${{ secrets.GITHUB_TOKEN }}\n";
        var before = new Dictionary<string, string?> { [".github/workflows/ci.yml"] = Head };

        RepairPolicy.Refusal([new PatchedFile(".github/workflows/ci.yml", Head.Replace("npm test", "npm test -- --seed 1", StringComparison.Ordinal))], before: before).Should().BeNull("the secret was already read by the pull request that is repaired");
        RepairPolicy.Refusal([new PatchedFile(".github/workflows/ci.yml", Head.Replace("      - run: npm test\n", string.Empty, StringComparison.Ordinal))], before: before).Should().Contain("removes 1 step");
        RepairPolicy.Refusal([new PatchedFile(".github/workflows/ci.yml", Head + "      - run: npm audit || true\n")], before: before).Should().Contain("weakens the pipeline");
    }

    [Fact]
    public void A_change_that_takes_a_restriction_of_the_token_out_is_refused()
    {
        static BranchChange Change(string patch) => new("c0ffee", 0, 1, [new BranchFile(".github/workflows/ci.yml", "modified", 2, patch)], FilesComplete: true);

        ChangePolicy.Refusal(Change("@@ -1,3 +1,1 @@\n on: push\n-permissions:\n-  contents: read")).Should().Contain("restriction of the token");
    }

    [Fact]
    public void A_repair_that_copies_a_weakening_line_the_head_already_holds_into_another_job_is_refused()
    {
        const string Head = "jobs:\n  lint:\n    continue-on-error: true\n    steps:\n      - run: npm run lint\n  test:\n    steps:\n      - run: npm test\n";
        var copied = Head.Replace("  test:\n", "  test:\n    continue-on-error: true\n", StringComparison.Ordinal);

        RepairPolicy.Refusal([new PatchedFile(".github/workflows/ci.yml", copied)], before: new Dictionary<string, string?> { [".github/workflows/ci.yml"] = Head }).Should().Contain("weakens the pipeline");
    }

    [Fact]
    public void A_repair_of_a_pipeline_that_the_forge_could_not_read_at_the_repaired_head_is_not_passed() =>
        RepairPolicy.Refusal([new PatchedFile(".github/workflows/ci.yml", "on: push\n")], before: new Dictionary<string, string?> { [".github/workflows/ci.yml"] = null }).Should().Contain("could not be read");

    [Fact]
    public void A_change_to_a_pipeline_file_that_does_not_say_why_is_not_kept()
    {
        PipelineChanges.Unjustified(["package.json", ".github/workflows/ci.yml"], null).Should().Contain(".github/workflows/ci.yml").And.Contain("does not say why");
        PipelineChanges.Unjustified([".github/workflows/ci.yml"], "  ").Should().NotBeNull("a blank justification says nothing");
        PipelineChanges.Unjustified([".github/workflows/ci.yml"], "p1 failed on the removed set-output command; the step now writes to GITHUB_OUTPUT").Should().BeNull("then the controller checks what it says");
        PipelineChanges.Unjustified(["package.json"], null).Should().BeNull("a change that touches no pipeline needs no justification");
    }
}
