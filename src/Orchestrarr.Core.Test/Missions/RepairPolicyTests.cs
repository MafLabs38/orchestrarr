using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

public sealed class RepairPolicyTests
{
    [Theory]
    [InlineData("test/api.test.js")]
    [InlineData("src/__tests__/a.js")]
    [InlineData("tests/test_app.py")]
    [InlineData("app/test_models.py")]
    [InlineData("CLAUDE.md")]
    [InlineData(".claude/settings.json")]
    [InlineData("pkg/handler_test.go")]
    [InlineData("src/App.Tests/OrderTests.cs")]
    [InlineData("spec/models/order_spec.rb")]
    [InlineData(".nvmrc")]
    [InlineData(".env.production")]
    [InlineData("CODEOWNERS")]
    [InlineData("config/secrets.enc.yaml")]
    public void What_decides_whether_it_passes_what_it_depends_on_and_how_it_is_built_are_not_the_agents(string path) =>
        RepairPolicy.IsProtected(path).Should().BeTrue();

    [Theory]
    [InlineData("server.js")]
    [InlineData("lib/items.js")]
    [InlineData("app.py")]
    [InlineData("src/routes/orders.ts")]
    public void The_source_of_the_application_is(string path) => RepairPolicy.IsProtected(path).Should().BeFalse();

    [Theory]
    [InlineData("a b.js")]
    [InlineData("../server.js")]
    [InlineData("/etc/passwd")]
    [InlineData("a//b.js")]
    [InlineData("a'b.js")]
    [InlineData("")]
    public void A_path_that_would_need_quoting_or_leaves_the_repository_is_not_carried(string path) => RepairPolicy.IsPlainPath(path).Should().BeFalse();

    [Fact]
    public void The_paths_that_the_frame_of_the_mission_forbids_are_forbidden_to_a_repair_too()
    {
        RepairPolicy.Refusal([new PatchedFile("infra/prod/deploy.js", "x")], ["infra/prod/**"]).Should().Contain("frame of the mission");
        RepairPolicy.Refusal([new PatchedFile("infra/prod/deploy.js", "x")]).Should().BeNull("the policy alone does not know the certificate's paths");
        RepairPolicy.Refusal([new PatchedFile("server.js", "x")], ["infra/prod/**"]).Should().BeNull();
    }

    [Theory]
    [InlineData("jest.setup.js")]
    [InlineData("vite.config.ts")]
    [InlineData("e2e/login.ts")]
    [InlineData("__snapshots__/a.snap")]
    [InlineData(".husky/pre-commit")]
    public void The_files_that_set_up_the_tests_and_the_build_are_protected_too(string path) => RepairPolicy.IsProtected(path).Should().BeTrue();

    [Fact]
    public void A_repair_of_the_source_is_accepted_and_the_limits_and_the_protected_paths_are_refused()
    {
        RepairPolicy.Refusal([new PatchedFile("server.js", "ok")]).Should().BeNull();
        RepairPolicy.Refusal([]).Should().Contain("between 1 and");
        RepairPolicy.Refusal([new PatchedFile("test/api.test.js", "x")]).Should().Contain("protected");
        RepairPolicy.Refusal([new PatchedFile("requirements.txt", "urllib3==2.2.3")]).Should().BeNull("a companion dependency is raised as a developer would (ADR 0055)");
        RepairPolicy.Refusal([new PatchedFile("a.js", "x"), new PatchedFile("a.js", "y")]).Should().Contain("twice");
        RepairPolicy.Refusal([new PatchedFile("a.js", new string('x', RepairPolicy.MaxFileBytes + 1))]).Should().Contain("too big");
        RepairPolicy.Refusal([new PatchedFile("a.js", "bin\0ary")]).Should().Contain("not text");
        RepairPolicy.Refusal([new PatchedFile(null!, "x")]).Should().Contain("no path");
        RepairPolicy.Refusal([new PatchedFile("a.js", null!)]).Should().Contain("no path or no content");
        RepairPolicy.Refusal([.. Enumerable.Range(0, RepairPolicy.MaxFiles + 1).Select(n => new PatchedFile($"f{n}.js", "x"))]).Should().Contain("between 1 and");
    }
}
