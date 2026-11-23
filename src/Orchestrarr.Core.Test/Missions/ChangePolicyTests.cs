using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

public sealed class ChangePolicyTests
{
    private static PatchedFile File(string path, string content = "x") => new(path, content);

    [Theory]
    [InlineData("package.json")]
    [InlineData("package-lock.json")]
    [InlineData("requirements.txt")]
    [InlineData("Dockerfile")]
    [InlineData("src/server.js")]
    [InlineData("test/server.test.js")]
    [InlineData("tests/new_test.py")]
    public void What_a_developer_would_change_to_solve_a_problem_may_leave_the_machine_the_dependencies_the_tests_and_the_build_included(string path)
    {
        ChangePolicy.Refusal([File(path)]).Should().BeNull();
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
    public void Never_the_pipelines_the_files_that_hold_a_secret_or_the_files_that_instruct_an_agent(string path)
    {
        ChangePolicy.Refusal([File(path)]).Should().Contain("protected");
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("a b.txt")]
    [InlineData("quote'.txt")]
    public void A_path_that_is_not_plain_is_refused_not_escaped(string path)
    {
        ChangePolicy.Refusal([File(path)]).Should().Contain("not one that a change may carry");
    }

    [Fact]
    public void The_frame_of_the_mission_adds_to_the_policy_and_the_size_the_count_and_the_text_are_bounded()
    {
        ChangePolicy.Refusal([File("infra/prod.tf")], ["infra/**"]).Should().Contain("forbidden by the frame");
        ChangePolicy.Refusal([]).Should().Contain("between 1 and");
        ChangePolicy.Refusal([.. Enumerable.Range(0, ChangePolicy.MaxFiles + 1).Select(index => File($"f{index}.txt"))]).Should().Contain("between 1 and");
        ChangePolicy.Refusal([File("big.txt", new string('x', ChangePolicy.MaxFileBytes + 1))]).Should().Contain("too big");
        ChangePolicy.Refusal([File("bin.dat", "a\0b")]).Should().Contain("not text");
        ChangePolicy.Refusal([File("a.txt"), File("a.txt")]).Should().Contain("twice");
        ChangePolicy.Refusal([new PatchedFile("a.txt", null!)]).Should().Contain("no path or no content");
    }

    [Fact]
    public void What_a_build_or_an_install_leaves_in_the_folder_is_an_artifact_and_not_the_change()
    {
        ChangePolicy.IsArtifact("node_modules/lodash/index.js").Should().BeTrue();
        ChangePolicy.IsArtifact("src/__pycache__/x.pyc").Should().BeTrue();
        ChangePolicy.IsArtifact("coverage/lcov.info").Should().BeTrue();
        ChangePolicy.IsArtifact("src/server.js").Should().BeFalse();
        ChangePolicy.IsArtifact("package-lock.json").Should().BeFalse();
    }
}
