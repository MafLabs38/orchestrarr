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
        Judge("on: [push, pull_request]\npermissions:\n  contents: read\njobs: {}").Should().Contain("removes the job 'test'");
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

    [Theory]
    [InlineData("on: [push, pull_request]", "on: [workflow_dispatch]", "stops the workflow from running")]
    [InlineData("on: [push, pull_request]", "on:\n  push:\n    paths-ignore: ['**']\n  pull_request:", "path filters of the trigger 'push'")]
    [InlineData("      - run: npm test", "      - run: npm test\n        shell: true {0}", "shell of its own")]
    public void A_workflow_that_no_longer_runs_where_it_ran_or_runs_its_steps_in_a_shell_of_its_own_is_refused(string from, string to, string reason) =>
        Judge(Base.Replace(from, to, StringComparison.Ordinal)).Should().Contain(reason);

    [Fact]
    public void A_known_shell_and_unchanged_filters_are_not_a_weakening()
    {
        const string Filtered = "on:\n  push:\n    paths: [src/**]\njobs:\n  t:\n    runs-on: x\n    steps:\n      - run: make\n        shell: bash";
        Judge(Filtered.Replace("run: make", "run: make test", StringComparison.Ordinal), Filtered).Should().BeNull();
    }

    [Fact]
    public void A_workflow_that_uses_yaml_aliases_is_not_read_and_the_change_is_refused()
    {
        var bomb = "on: push\na: &a [x, x, x, x, x, x, x, x, x]\nb: &b [*a, *a, *a, *a, *a, *a, *a, *a, *a]\nc: &c [*b, *b, *b, *b, *b, *b, *b, *b, *b]\nd: [*c, *c, *c, *c, *c, *c, *c, *c, *c]\njobs: {}";

        Judge(bomb).Should().Contain("does not read as a workflow", "an alias is refused before its graph is walked");
    }

    [Fact]
    public void A_workflow_nested_deeper_than_any_workflow_is_not_read_and_a_huge_matrix_is_not_read_as_a_small_one()
    {
        Judge("on: push\nx: " + new string('[', 500) + new string(']', 500) + "\njobs: {}").Should().Contain("does not read as a workflow");

        var axes = string.Join("\n", Enumerable.Range(0, 12).Select(axis => $"        a{axis}: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]"));
        var huge = $"on: push\njobs:\n  t:\n    runs-on: x\n    strategy:\n      matrix:\n{axes}\n    steps:\n      - run: make";
        Judge(huge, huge).Should().BeNull("the same matrix, whatever its size, is not a narrowing");
    }

    [Fact]
    public void A_workflow_whose_base_does_not_read_is_refused_rather_than_compared_with_nothing() =>
        Judge(Base, "on: push\na: &a [x]\nb: *a\njobs: {}").Should().Contain("cannot be told");

    [Fact]
    public void A_dead_path_filter_mended_to_cover_the_code_is_a_fix_and_any_other_filter_change_is_refused()
    {
        string[] files = ["src/feature.js", "src/feature.test.js", "README.md"];
        const string Dead = "on:\n  push:\n    paths: ['source/**']\njobs:\n  t:\n    runs-on: x\n    steps:\n      - run: make";
        var live = Dead.Replace("source/**", "src/**", StringComparison.Ordinal);

        WorkflowStructure.Weakening(Path, Dead, live, files).Should().BeNull("the filter covered no file; it now covers the code: the control is mended, not weakened");
        WorkflowStructure.Weakening(Path, live, Dead.Replace("source/**", "src/*.test.js", StringComparison.Ordinal), files).Should().Contain("was running on files", "a filter that ran is never narrowed");
        WorkflowStructure.Weakening(Path, live, Dead.Replace("['source/**']", "['src/**', 'lib/**']", StringComparison.Ordinal), files).Should().Contain("was running on files", "only a dead filter may change");
        WorkflowStructure.Weakening(Path, Dead, Dead.Replace("source/**", "lib/**", StringComparison.Ordinal), files).Should().Contain("still covers no file");
        WorkflowStructure.Weakening(Path, Dead, live).Should().Contain("cannot be measured", "without the files of the repository a filter change is not judged");
        WorkflowStructure.Weakening(Path, Dead, Dead.Replace("    paths: ['source/**']", "    branches: [main]\n    paths: ['source/**']", StringComparison.Ordinal), files).Should().Contain("branches or tags");
    }

    [Theory]
    [InlineData("['sr?/**']")]
    [InlineData("['src/**', '!src/[a-z]*.js']")]
    [InlineData("['src/fea+ture.js']")]
    [InlineData("[\"\\x02\"]")]
    [InlineData("{ src: yes }")]
    [InlineData("[[src/**]]")]
    public void A_path_filter_the_forge_reads_otherwise_is_not_measured(string patterns)
    {
        string[] files = ["src/feature.js"];
        const string Dead = "on:\n  push:\n    paths: ['source/**']\njobs:\n  t:\n    runs-on: x\n    steps:\n      - run: make";

        WorkflowStructure.Weakening(Path, Dead, Dead.Replace("['source/**']", patterns, StringComparison.Ordinal), files).Should().Contain("cannot be measured exactly");
    }

    [Fact]
    public void A_trigger_with_both_kinds_of_path_filter_is_not_measured()
    {
        const string Dead = "on:\n  push:\n    paths: ['source/**']\njobs:\n  t:\n    runs-on: x\n    steps:\n      - run: make";

        WorkflowStructure.Weakening(Path, Dead, Dead.Replace("    paths: ['source/**']", "    paths: ['src/**']\n    paths-ignore: ['src/x/**']", StringComparison.Ordinal), ["src/feature.js"]).Should().Contain("cannot be measured exactly");
    }

    private static string Push(string filters) => $"on:\n  push:\n    {filters}\njobs:\n  t:\n    runs-on: x\n    steps:\n      - run: make";

    [Theory]
    [InlineData("paths: ['sr?/**']", "paths: ['src/feature.js']", "cannot be measured exactly")]
    [InlineData("paths-ignore: ['**']", "paths-ignore: ['docs/**']", null)]
    [InlineData("paths-ignore: ['docs/**']", "paths-ignore: ['src/**']", "was running on files")]
    [InlineData("paths: ['src/**', '!src/**']", "paths: ['src/**']", null)]
    [InlineData("paths: ['!src/**', 'src/**']", "paths: ['src/feature.js']", "was running on files")]
    [InlineData("paths: []", "paths: ['src/**']", "cannot be measured exactly")]
    [InlineData("paths: ['!docs/**']", "paths: ['README.md']", "cannot be measured exactly")]
    [InlineData("paths-ignore: []", "paths-ignore: ['docs/**']", "cannot be measured exactly")]
    [InlineData("paths: ['./src/**']", "paths: ['src/**']", null)]
    [InlineData("paths-ignore: ['!src/**']", "paths-ignore: ['docs/**']", "cannot be measured exactly")]
    public void A_change_of_a_path_filter_is_judged_on_what_it_covered_and_what_it_covers(string before, string after, string? refusal)
    {
        string[] files = ["src/feature.js", "docs/a.md"];

        var judged = WorkflowStructure.Weakening(Path, Push(before), Push(after), files);

        if (refusal is null)
        {
            judged.Should().BeNull();
        }
        else
        {
            judged.Should().Contain(refusal);
        }
    }

    [Fact]
    public void A_filter_added_to_a_trigger_that_had_none_narrows_it_and_a_filter_past_its_bounds_is_not_measured()
    {
        string[] files = ["src/feature.js"];
        const string Bare = "on: [push]\njobs:\n  t:\n    runs-on: x\n    steps:\n      - run: make";

        WorkflowStructure.Weakening(Path, Bare, Push("paths: ['src/**']"), files).Should().Contain("was running on files", "a trigger with no filter runs for every file");
        var many = "paths: [" + string.Join(", ", Enumerable.Range(0, 51).Select(index => $"'src/a{index}'")) + ", 'src/**']";
        WorkflowStructure.Weakening(Path, Push("paths: ['source/**']"), Push(many), files).Should().Contain("cannot be measured exactly");
        WorkflowStructure.Weakening(Path, Push("paths: ['source/**']"), Push($"paths: ['src/{new string('a', 200)}', 'src/**']"), files).Should().Contain("cannot be measured exactly");
    }

    [Fact]
    public void A_dormant_trigger_that_runs_with_the_base_credentials_is_never_woken_and_an_action_is_mended_only_by_another_version_of_itself()
    {
        string[] files = ["src/feature.js"];
        const string Dormant = "on:\n  pull_request_target:\n    paths: ['old/**']\njobs:\n  t:\n    runs-on: x\n    steps:\n      - run: make";

        WorkflowStructure.Weakening(Path, Dormant, Dormant.Replace("old/**", "src/**", StringComparison.Ordinal), files).Should().Contain("runs with the base's credentials");
        Judge(Base.Replace("actions/checkout@v4", "actions/checkout@v5", StringComparison.Ordinal)).Should().BeNull("another version of the same action mends it");
        Judge(Base.Replace("actions/checkout@v4", "someone/checkout@v1", StringComparison.Ordinal)).Should().Contain("replaces the action 'actions/checkout' with 'someone/checkout'");
    }

    [Theory]
    [InlineData("permissions:\n  contents: write\n")]
    [InlineData("permissions: write-all\n")]
    [InlineData("env:\n  TOKEN: ${{ secrets.NPM_TOKEN }}\n")]
    public void A_dormant_workflow_that_can_write_or_reads_a_secret_is_never_woken(string powers)
    {
        var dormant = "on:\n  push:\n    paths: ['old/**']\n" + powers + "jobs:\n  t:\n    runs-on: x\n    steps:\n      - run: make";

        WorkflowStructure.Weakening(Path, dormant, dormant.Replace("old/**", "src/**", StringComparison.Ordinal), ["src/feature.js"]).Should().Contain("can write or reads a secret");
    }
}
