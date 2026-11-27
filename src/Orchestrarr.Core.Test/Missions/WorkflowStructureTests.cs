using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

/// <summary>A workflow is read as YAML and compared before and after a change (ADR 0057): whatever the spelling, a change that checks less or runs with more rights is refused, and a change that fixes the pipeline is not.</summary>
public sealed class WorkflowStructureTests
{
    private const string Path = ".github/workflows/ci.yml";

    private const string Base = """
        on: [push, pull_request]
        permissions:
          contents: read
        jobs:
          test:
            runs-on: ubuntu-latest
            strategy:
              matrix:
                node: [20, 22]
            steps:
              - uses: actions/checkout@v4
              - run: npm ci
              - run: npm test
        """;

    private static string? Judge(string after, string? before = Base) => WorkflowStructure.Weakening(Path, before, after);

    [Fact]
    public void A_change_that_fixes_the_pipeline_without_checking_less_is_accepted()
    {
        Judge(Base.Replace("actions/checkout@v4", "actions/checkout@v5", StringComparison.Ordinal).Replace("run: npm test", "run: npm test -- --ci", StringComparison.Ordinal)).Should().BeNull();
        Judge(Base + "\n      - run: npm run lint").Should().BeNull("a step more checks more");
        Judge(Base.Replace("node: [20, 22]", "node: [20, 22, 24]", StringComparison.Ordinal)).Should().BeNull("a wider matrix checks more");
    }

    [Theory]
    [InlineData("\n      - run: npm test", "", "removes 1 step")]
    [InlineData("      - run: npm test", "      - run: npm test\n        continue-on-error: true", "fail without failing")]
    [InlineData("      - run: npm test", "      - run: npm test\n        continue-on-error: ${{ matrix.node == 20 }}", "fail without failing")]
    [InlineData("      - run: npm test", "      - run: npm test\n        if: ${{ false }}", "run only sometimes")]
    [InlineData("      - run: npm test", "      - run: npm test\n        if: github.event_name == 'push'", "run only sometimes")]
    [InlineData("    runs-on: ubuntu-latest", "    runs-on: ubuntu-latest\n    if: 0", "changes when the job")]
    [InlineData("node: [20, 22]", "node: [22]", "narrows the matrix")]
    [InlineData("  contents: read", "  contents: write", "widens the token")]
    [InlineData("permissions:\n  contents: read\n", "", "restriction of the token")]
    [InlineData("on: [push, pull_request]", "on: [push, pull_request_target]", "pull_request_target")]
    public void A_change_that_checks_less_or_runs_with_more_rights_is_refused_in_any_spelling(string from, string to, string reason)
    {
        var after = Base.Replace(from.Replace("\n", Environment.NewLine, StringComparison.Ordinal), to.Replace("\n", Environment.NewLine, StringComparison.Ordinal), StringComparison.Ordinal);
        after.Should().NotBe(Base, "the case must change the workflow");

        Judge(after).Should().Contain(reason);
    }

    [Fact]
    public void Removing_a_job_is_refused_and_a_new_job_that_cannot_fail_is_too()
    {
        Judge("on: push\npermissions:\n  contents: read\njobs: {}").Should().Contain("removes the job 'test'");
        Judge(Base + "\n  extra:\n    runs-on: ubuntu-latest\n    continue-on-error: true\n    steps:\n      - run: make check").Should().Contain("job 'extra'");
    }

    [Fact]
    public void A_new_workflow_is_judged_whole_and_one_that_does_not_read_is_refused()
    {
        WorkflowStructure.Weakening(Path, null, "on: push\npermissions: write-all\njobs:\n  a:\n    runs-on: x\n    steps:\n      - run: make").Should().Contain("every right");
        WorkflowStructure.Weakening(Path, null, "on: push\njobs:\n  a:\n    runs-on: x\n    steps:\n      - run: make").Should().BeNull();
        Judge("jobs: [unclosed").Should().Contain("does not read as a workflow");
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml", true)]
    [InlineData(".github/workflows/release.yaml", true)]
    [InlineData(".github/dependabot.yml", false)]
    [InlineData("services/api/.github/workflows/ci.yml", false)]
    [InlineData(".gitlab-ci.yml", false)]
    public void Only_the_workflows_that_the_forge_runs_are_read_as_workflows(string path, bool workflow) =>
        WorkflowStructure.IsWorkflow(path).Should().Be(workflow);

    [Fact]
    public void The_text_of_a_step_is_still_read_for_what_only_text_says()
    {
        PipelineChanges.Guard(Path, Base, Base.Replace("run: npm test", "run: npm test || true", StringComparison.Ordinal), ["              - run: npm test || true"], []).Should().Contain("|| true");
        PipelineChanges.Guard(Path, Base, Base.Replace("run: npm test", "run: echo ${{ secrets.TOKEN }}", StringComparison.Ordinal), ["              - run: echo ${{ secrets.TOKEN }}"], []).Should().Contain("secrets");
    }

    [Fact]
    public void The_matrix_is_counted_in_runs_and_a_token_given_rights_where_it_had_the_default_is_wider()
    {
        const string Two = "on: push\njobs:\n  t:\n    runs-on: x\n    strategy:\n      matrix:\n        os: [a, b]\n        node: [20, 22]\n    steps:\n      - run: make";
        Judge(Two.Replace("os: [a, b]", "os: [a, b]\n        include:\n          - os: c\n            node: 24", StringComparison.Ordinal), Two).Should().BeNull("an included run adds one");
        Judge(Two.Replace("node: [20, 22]", "node: [20, 22]\n        exclude:\n          - os: a\n            node: 20", StringComparison.Ordinal), Two).Should().Contain("narrows the matrix", "an excluded run is one less");
        Judge(Two.Replace("os: [a, b]", "os: [a, b, c]", StringComparison.Ordinal).Replace("node: [20, 22]", "node: [22]", StringComparison.Ordinal), Two).Should().Contain("narrows the matrix", "three by one is fewer runs than two by two");

        const string Bare = "on: push\njobs:\n  t:\n    runs-on: x\n    steps:\n      - run: make";
        Judge(Bare.Replace("on: push", "on: push\npermissions:\n  contents: write", StringComparison.Ordinal), Bare).Should().Contain("contents: write", "where nothing was said the token had the read-only default");
        Judge(Bare.Replace("on: push", "on: push\npermissions:\n  contents: read", StringComparison.Ordinal), Bare).Should().BeNull("a narrower token is not a weakening");
    }
}
