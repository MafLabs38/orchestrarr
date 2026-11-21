using Orchestrarr.MissionRunner;

namespace Orchestrarr.MissionRunner.Test;

public sealed class StagingProofTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly string Head = new('a', 40);

    private sealed class ScriptedShell(Func<string, IReadOnlyList<string>, ShellResult> answer) : IShell
    {
        public List<string> Calls { get; } = [];

        public Task<ShellResult> RunAsync(string file, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add($"{file} {string.Join(' ', arguments)}");
            return Task.FromResult(answer(file, arguments));
        }
    }

    private static MissionRunnerOptions Options(string? lab = "/lab/lab.sh") => new() { StagingMachine = "leipzig", LabTool = lab };

    private static ClaimedMission Mission(string repository = "example/shop", int? pullRequest = 7, string? head = null) =>
        new("m1", "p1", "example/shop", "Staging", "Prove it", repository, pullRequest, head ?? Head, DateTimeOffset.UtcNow.AddHours(1), []);

    private static ShellResult Ok(string output = "") => new(0, output, TimedOut: false);

    // A staging machine that has the pull request at <Head>, the given files, and a build that ends with the given code
    private static ScriptedShell Machine(string files, int buildExit = 0, string head = "")
    {
        var sha = head.Length > 0 ? head : Head;
        return new ScriptedShell((file, arguments) =>
        {
            if (file != "ssh")
            {
                return Ok();
            }
            var script = arguments[^1];
            if (script.Contains("git clone", StringComparison.Ordinal))
            {
                return Ok($"HEAD {sha}\n package.json | 2 +-\n 1 file changed\n");
            }
            if (script.Contains("ls -1", StringComparison.Ordinal))
            {
                return Ok(files);
            }
            return new ShellResult(buildExit, buildExit == 0 ? "4 passing" : "1 failing", TimedOut: false);
        });
    }

    [Fact]
    public async Task A_pull_request_that_builds_and_passes_is_handed_over_with_its_commit_and_the_machine_is_restored_twice()
    {
        var shell = Machine("package.json\npackage-lock.json\n");
        var lines = new List<string>();

        var dossier = await new StagingProof(shell, Options()).RunAsync(Mission(), line => { lines.Add(line); return Task.CompletedTask; }, Ct);

        dossier.Claim.Should().Contain("builds and its tests pass");
        dossier.Commit.Should().Be(Head);
        dossier.Complex.Should().BeFalse();
        dossier.OpenPoints.Should().BeEmpty();
        dossier.EvidenceDigests.Should().Contain(digest => digest.StartsWith("node:exit=0:sha256=", StringComparison.Ordinal));
        shell.Calls.Where(call => call.StartsWith("/lab/lab.sh reset leipzig", StringComparison.Ordinal)).Should().HaveCount(2, "before the work and after it");
        shell.Calls.Should().Contain(call => call.Contains("npm ci", StringComparison.Ordinal));
        lines.Should().Contain(line => line.Contains("restored", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_build_that_fails_says_so_and_keeps_the_end_of_the_output()
    {
        var dossier = await new StagingProof(Machine("requirements.txt\n", buildExit: 1), Options()).RunAsync(Mission(), _ => Task.CompletedTask, Ct);

        dossier.Claim.Should().Contain("does NOT pass").And.Contain("python");
        dossier.OpenPoints.Should().ContainSingle().Which.Should().Contain("1 failing");
        dossier.Complex.Should().BeFalse("a red build is a result, not a limit of the worker");
    }

    [Fact]
    public async Task A_pull_request_that_moved_since_the_approval_is_not_built()
    {
        var shell = Machine("package.json\n", head: new string('b', 40));

        var dossier = await new StagingProof(shell, Options()).RunAsync(Mission(), _ => Task.CompletedTask, Ct);

        dossier.Claim.Should().StartWith("Nothing was proven");
        dossier.Complex.Should().BeTrue();
        shell.Calls.Should().NotContain(call => call.Contains("docker run", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("example/shop; rm -rf ~", 7)]
    [InlineData("example", 7)]
    [InlineData("example/shop", null)]
    public async Task A_mission_that_names_no_pull_request_or_an_unreadable_repository_is_refused_without_touching_the_machine(string repository, int? number)
    {
        var shell = Machine("package.json\n");

        var dossier = await new StagingProof(shell, Options()).RunAsync(Mission(repository, number), _ => Task.CompletedTask, Ct);

        dossier.Claim.Should().StartWith("Nothing was proven");
        shell.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_runner_with_no_way_to_restore_the_machine_does_not_work_on_it()
    {
        var shell = Machine("package.json\n");

        var dossier = await new StagingProof(shell, Options(lab: null)).RunAsync(Mission(), _ => Task.CompletedTask, Ct);

        dossier.OpenPoints.Should().ContainSingle().Which.Should().Contain("cannot restore");
        shell.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_machine_that_cannot_be_restored_stops_the_work()
    {
        var shell = new ScriptedShell((file, _) => file == "ssh" ? Ok() : new ShellResult(1, "no such vm", TimedOut: false));

        var dossier = await new StagingProof(shell, Options()).RunAsync(Mission(), _ => Task.CompletedTask, Ct);

        dossier.Claim.Should().Contain("could not be restored");
        shell.Calls.Should().NotContain(call => call.StartsWith("ssh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_repository_with_nothing_to_build_is_handed_over_as_complex()
    {
        var dossier = await new StagingProof(Machine("README.md\n"), Options()).RunAsync(Mission(), _ => Task.CompletedTask, Ct);

        dossier.Complex.Should().BeTrue();
        dossier.ComplexReason.Should().Contain("No build recipe");
    }

    [Theory]
    [InlineData("package.json", "node")]
    [InlineData("requirements.txt", "python")]
    [InlineData("compose.yaml", "compose")]
    [InlineData("README.md", "none")]
    public void The_recipe_follows_the_files_of_the_repository(string file, string kind) =>
        StagingProof.Recipe(new HashSet<string> { file }).Kind.Should().Be(kind);
}
