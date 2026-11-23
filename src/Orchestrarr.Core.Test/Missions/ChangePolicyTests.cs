using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

/// <summary>The policy judges what the forge says a branch holds, never what a machine says of it.</summary>
public sealed class ChangePolicyTests
{
    private const string Base = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static BranchFile File(string path, string status = "modified", int changes = 2, string? patch = "@@ -1 +1 @@\n-a\n+b") => new(path, status, changes, patch);

    private static BranchChange Change(params BranchFile[] files) => new(Base, 0, 1, files, true);

    [Theory]
    [InlineData("package.json")]
    [InlineData("package-lock.json")]
    [InlineData("requirements.txt")]
    [InlineData("Dockerfile")]
    [InlineData("src/server.js")]
    [InlineData("test/server.test.js")]
    [InlineData("tests/new_test.py")]
    public void What_a_developer_would_change_to_solve_a_problem_may_be_integrated_the_dependencies_the_tests_and_the_build_included(string path)
    {
        ChangePolicy.Refusal(Change(File(path))).Should().BeNull();
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".gitlab-ci.yml")]
    [InlineData("deploy/.env")]
    [InlineData("secrets/db.txt")]
    [InlineData("config/server.key")]
    [InlineData("CLAUDE.md")]
    [InlineData(".claude/settings.json")]
    [InlineData(".git/config")]
    [InlineData(".npmrc")]
    [InlineData("frontend/.yarnrc.yml")]
    [InlineData("pip.conf")]
    [InlineData(".gitattributes")]
    public void Never_the_pipelines_the_files_that_hold_a_secret_or_the_files_that_instruct_an_agent(string path)
    {
        ChangePolicy.Refusal(Change(File(path))).Should().Contain("protected");
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("a b.txt")]
    [InlineData("quote'.txt")]
    public void A_path_that_is_not_plain_is_refused_not_escaped(string path)
    {
        ChangePolicy.Refusal(Change(File(path))).Should().Contain("not one that a change may carry");
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("renamed")]
    [InlineData("copied")]
    public void A_deletion_or_a_rename_is_not_a_change_that_is_carried(string status)
    {
        ChangePolicy.Refusal(Change(File("src/a.js", status))).Should().Contain("added and modified");
    }

    [Fact]
    public void The_frame_of_the_mission_adds_to_the_policy()
    {
        ChangePolicy.Refusal(Change(File("infra/prod.tf")), ["infra/**"]).Should().Contain("forbidden by the frame");
    }

    [Fact]
    public void The_count_and_the_completeness_of_the_list_are_bounded()
    {
        ChangePolicy.Refusal(Change()).Should().Contain("between 1 and");
        ChangePolicy.Refusal(Change([.. Enumerable.Range(0, ChangePolicy.MaxFiles + 1).Select(index => File($"f{index}.txt"))])).Should().Contain("between 1 and");
        ChangePolicy.Refusal(Change(File("a.txt")) with { FilesComplete = false }).Should().Contain("between 1 and");
    }

    [Fact]
    public void A_branch_that_does_not_contain_the_default_branch_is_not_integrated_the_change_is_made_again()
    {
        ChangePolicy.Refusal(Change(File("a.txt")) with { BehindBy = 3 }).Should().Contain("moved");
    }

    [Fact]
    public void A_file_with_no_patch_is_not_text_or_too_big_unless_a_tool_wrote_it()
    {
        ChangePolicy.Refusal(Change(File("logo.png", "added", 0, null))).Should().BeNull("a binary file has no change count and no patch: nothing says it is not text");
        ChangePolicy.Refusal(Change(File("data.bin", "added", 40, null))).Should().Contain("too big, or is not text");
        ChangePolicy.Refusal(Change(File("package-lock.json", "modified", 5000, null))).Should().BeNull("the forge gives no patch for a very large generated file");
        ChangePolicy.Refusal(Change(File("big.txt", "modified", 1, new string('x', ChangePolicy.MaxFileBytes + 1)))).Should().Contain("too big");
    }

    [Fact]
    public void The_total_size_of_the_patches_is_bounded()
    {
        var files = Enumerable.Range(0, 8).Select(index => File($"f{index}.txt", "modified", 1, new string('x', 600_000))).ToArray();

        ChangePolicy.Refusal(Change(files)).Should().Contain("too big");
    }

    [Fact]
    public void The_controller_reads_the_size_of_every_file_and_the_lines_of_those_that_a_tool_did_not_write_and_a_cut_is_said()
    {
        var reading = ChangePolicy.Reading(Change(
            File("package.json", patch: "@@ -1 +1 @@\n-\"lodash\": \"4.17.4\"\n+\"lodash\": \"4.18.1\""),
            File("package-lock.json", "modified", 900, "@@\n-x\n+y")));

        reading.Should().Contain("package.json (modified, 2 lines)").And.Contain("package-lock.json").And.Contain("\"lodash\": \"4.18.1\"").And.Contain("Generated files changed too (their lines are not shown): package-lock.json");
        reading.Should().NotContain("[CUT");
        ChangePolicy.Reading(Change([.. Enumerable.Range(0, 41).Select(index => File($"f{index}.txt"))])).Should().Contain("[CUT: the rest is not shown]");
    }

    [Fact]
    public void A_floor_path_of_the_frame_that_this_policy_does_not_name_is_refused_too()
    {
        ChangePolicy.IsProtected(".github/workflows/x.yml").Should().BeTrue();
        ChangePolicy.IsProtected("src/server.js").Should().BeFalse();
    }
}
