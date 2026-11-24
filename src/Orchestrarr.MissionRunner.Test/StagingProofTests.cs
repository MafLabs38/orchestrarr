using Orchestrarr.Core.Missions;
using Orchestrarr.MissionRunner;

namespace Orchestrarr.MissionRunner.Test;

public sealed class StagingProofTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly string Head = new('a', 40);
    private static readonly string Tree = new('7', 40);

    private sealed class ScriptedShell(Func<string, IReadOnlyList<string>, ShellResult> answer) : IShell
    {
        public List<string> Calls { get; } = [];

        public Task<ShellResult> RunAsync(string file, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add($"{file} {string.Join(' ', arguments)}");
            return Task.FromResult(answer(file, arguments));
        }
    }

    private static MissionRunnerOptions Options(string? lab = "/lab/lab.sh") => new() { StagingMachine = "staging-1", LabTool = lab, Repositories = ["example/*"] };

    private static ClaimedMission Mission(string repository = "example/shop", int? pullRequest = 7, string? head = "") =>
        new("m1", "p1", "example/shop", "Staging", "Prove it", repository, pullRequest, head == "" ? Head : head, DateTimeOffset.UtcNow.AddHours(1), []);

    private static ShellResult Ok(string output = "") => new(0, output, TimedOut: false);

    // What the staging machine answers to each step, with the build left to the caller
    private static ShellResult Step(string script, string files, Func<ShellResult> build, bool cloneFails = false)
    {
        if (script.Contains("git clone", StringComparison.Ordinal))
        {
            return cloneFails ? new ShellResult(1, "fatal: CONFLICT (content): Merge conflict in package-lock.json", TimedOut: false) : Ok($"HEAD {Head}\nTREE {Tree}\n");
        }
        if (script.Contains("ls -1", StringComparison.Ordinal))
        {
            return Ok(files);
        }
        return build();
    }

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
                return Ok($"HEAD {sha}\nTREE {Tree}\n package.json | 2 +-\n 1 file changed\n");
            }
            if (script.Contains("ls -1", StringComparison.Ordinal))
            {
                return Ok(files);
            }
            return new ShellResult(buildExit, buildExit == 0 ? "4 passing" : "1 failing", TimedOut: false);
        });
    }

    [Fact]
    public async Task A_pull_request_that_builds_and_passes_is_handed_over_with_its_commit_and_the_proof_does_not_restore_the_machine_itself()
    {
        var shell = Machine("package.json\npackage-lock.json\n");
        var lines = new List<string>();

        var dossier = await new StagingProof(shell, Options()).RunAsync(Mission(), line => { lines.Add(line); return Task.CompletedTask; }, Ct);

        dossier.Claim.Should().Contain("builds and its tests pass");
        dossier.Commit.Should().Be(Head);
        dossier.Complex.Should().BeFalse();
        dossier.OpenPoints.Should().BeEmpty();
        dossier.EvidenceDigests.Should().NotBeNull().And.Contain(digest => digest.StartsWith("node:exit=0:sha256=", StringComparison.Ordinal));
        dossier.EvidenceDigests.Should().Contain($"artifact:tree={Tree}", "the release has to be the tree that was proved here");
        shell.Calls.Should().Contain(call => call.Contains("merge --quiet --no-edit --no-ff pr", StringComparison.Ordinal), "what is built is the result of the merge on the base as it is now");
        shell.Calls.Should().NotContain(call => call.StartsWith("/lab/lab.sh", StringComparison.Ordinal), "the machine is restored and qualified by the platform step, between two jobs (ADR 0047)");
        shell.Calls.Should().Contain(call => call.Contains("npm ci", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pull_request_with_no_merge_result_is_refused_before_anything_is_built()
    {
        var shell = new ScriptedShell((file, arguments) => file != "ssh" ? Ok() : Step(arguments[^1], "package.json\n", () => Ok(string.Empty), cloneFails: true));

        var dossier = await new StagingProof(shell, Options()).RunAsync(Mission(), _ => Task.CompletedTask, Ct);

        dossier.Claim.Should().StartWith("Nothing was proven");
        dossier.EvidenceDigests.Should().Contain(MissionEvidence.RefusedConflict, "a conflict is said by a marker, so that the control plane ends the problem without a send-back");
        shell.Calls.Should().NotContain(call => call.Contains("docker run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_build_that_fails_says_so_and_keeps_the_end_of_the_output()
    {
        var shell = Machine("requirements.txt\n", buildExit: 1);

        var dossier = await new StagingProof(shell, Options()).RunAsync(Mission(), _ => Task.CompletedTask, Ct);

        dossier.Claim.Should().Contain("does NOT pass").And.Contain("python");
        dossier.OpenPoints.Should().ContainSingle().Which.Should().Contain("1 failing");
        dossier.Complex.Should().BeFalse("a red build is a result, not a limit of the worker");
        shell.Calls.Should().NotContain(call => call.StartsWith("/lab/lab.sh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_build_that_times_out_is_not_a_pass()
    {
        var shell = new ScriptedShell((file, arguments) => file != "ssh" ? Ok() : Step(arguments[^1], "package.json\n", () => new ShellResult(-1, "still running", TimedOut: true)));

        var dossier = await new StagingProof(shell, Options()).RunAsync(Mission(), _ => Task.CompletedTask, Ct);

        dossier.Claim.Should().Contain("does NOT pass").And.Contain("timed out");
    }

    [Fact]
    public async Task A_stop_during_the_build_ends_the_work()
    {
        var shell = new ScriptedShell((file, arguments) => file != "ssh" ? Ok() : Step(arguments[^1], "package.json\n", () => throw new OperationCanceledException()));

        var work = () => new StagingProof(shell, Options()).RunAsync(Mission(), _ => Task.CompletedTask, Ct);

        await work.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_pull_request_that_moved_since_the_approval_is_not_built()
    {
        var shell = Machine("package.json\n", head: new string('b', 40));

        var dossier = await new StagingProof(shell, Options()).RunAsync(Mission(), _ => Task.CompletedTask, Ct);

        dossier.Claim.Should().StartWith("Nothing was proven");
        dossier.Complex.Should().BeTrue();
        dossier.Commit.Should().BeNull("a refusal names no commit: nothing was proven at any");
        dossier.EvidenceDigests.Should().Contain(MissionEvidence.RefusedMoved);
        shell.Calls.Should().NotContain(call => call.Contains("docker run", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("example/shop; rm -rf ~", 7)]
    [InlineData("example", 7)]
    [InlineData("example/..", 7)]
    [InlineData("-bad/shop", 7)]
    [InlineData("example/shop", null)]
    public async Task A_mission_that_names_no_pull_request_or_an_unreadable_repository_is_refused_without_touching_the_machine(string repository, int? number)
    {
        var shell = Machine("package.json\n");

        var dossier = await new StagingProof(shell, Options()).RunAsync(Mission(repository, number), _ => Task.CompletedTask, Ct);

        dossier.Claim.Should().StartWith("Nothing was proven");
        shell.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_repository_that_the_runner_does_not_accept_is_refused_whatever_the_control_plane_says()
    {
        var shell = Machine("package.json\n");

        var dossier = await new StagingProof(shell, Options()).RunAsync(Mission("another/shop"), _ => Task.CompletedTask, Ct);

        dossier.Claim.Should().StartWith("Nothing was proven");
        dossier.OpenPoints.Should().ContainSingle().Which.Should().Contain("does not accept");
        shell.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_mission_that_names_no_commit_is_refused_without_touching_the_machine()
    {
        var shell = Machine("package.json\n");
        var mission = Mission() with { HeadSha = null };

        var dossier = await new StagingProof(shell, Options()).RunAsync(mission, _ => Task.CompletedTask, Ct);

        dossier.Claim.Should().StartWith("Nothing was proven");
        shell.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_runner_that_is_not_told_which_machine_to_use_does_not_guess()
    {
        var shell = Machine("package.json\n");

        var dossier = await new StagingProof(shell, new MissionRunnerOptions { LabTool = "/lab/lab.sh" }).RunAsync(Mission(), _ => Task.CompletedTask, Ct);

        dossier.Claim.Should().StartWith("Nothing was proven");
        shell.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_build_runs_with_no_capability_and_bounded_resources()
    {
        var shell = Machine("package.json\n");

        await new StagingProof(shell, Options()).RunAsync(Mission(), _ => Task.CompletedTask, Ct);

        shell.Calls.Should().Contain(call => call.Contains("--cap-drop ALL", StringComparison.Ordinal) && call.Contains("no-new-privileges", StringComparison.Ordinal) && call.Contains("--pids-limit", StringComparison.Ordinal) && call.Contains("--memory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_repository_with_nothing_to_build_is_handed_over_as_complex()
    {
        var dossier = await new StagingProof(Machine("README.md\n"), Options()).RunAsync(Mission(), _ => Task.CompletedTask, Ct);

        dossier.Complex.Should().BeTrue();
        dossier.ComplexReason.Should().Contain("No build recipe");
    }

    [Fact]
    public void A_repository_with_both_kinds_of_files_is_built_as_node()
    {
        var recipe = StagingProof.Recipe(new HashSet<string> { "requirements.txt", "package.json", "package-lock.json" });

        recipe.Kind.Should().Be("node");
        recipe.Command.Should().Contain("npm ci");
    }

    [Theory]
    [InlineData("package.json", "node")]
    [InlineData("requirements.txt", "python")]
    [InlineData("compose.yaml", "compose")]
    [InlineData("README.md", "none")]
    public void The_recipe_follows_the_files_of_the_repository(string file, string kind) =>
        StagingProof.Recipe(new HashSet<string> { file }).Kind.Should().Be(kind);

    [Fact]
    public async Task A_change_that_does_not_pass_is_repaired_by_the_agent_and_the_dossier_carries_the_files_and_the_tree_of_the_repaired_state()
    {
        var repaired = new string('6', 40);
        var shell = new RepairingShell(Tree, repaired);
        var options = Options();
        options.Repair = new RepairSettings { DockerfileFile = WriteTemp("FROM scratch"), TokenFile = WriteTemp("token"), Attempts = 1 };

        var dossier = await new StagingProof(shell, options, new RepairAgent(shell, options)).RunAsync(Mission(), _ => Task.CompletedTask, Ct);

        dossier.Claim.Should().Contain("did not pass").And.Contain("worker agent repaired").And.Contain("server.js");
        dossier.Files.Should().ContainSingle().Which.Path.Should().Be("server.js");
        dossier.EvidenceDigests.Should().Contain($"artifact:tree={repaired}").And.NotContain($"artifact:tree={Tree}", "the artifact is the repaired state");
        dossier.WhatChangedAndWhy.Should().Contain("the runner ran the steps again itself").And.Contain("4 passing");
        dossier.Commit.Should().Be(Head, "the repair starts from the commit that was approved");
    }

    [Fact]
    public async Task Without_an_agent_or_with_a_build_that_timed_out_nothing_is_repaired()
    {
        var shell = Machine("package.json\n", buildExit: 1);

        var plain = await new StagingProof(shell, Options()).RunAsync(Mission(), _ => Task.CompletedTask, Ct);

        plain.Files.Should().BeNull();
        plain.Claim.Should().Contain("does NOT pass");
    }

    private static string WriteTemp(string content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        return path;
    }

    // A staging machine on which the first build fails, the agent changes server.js, and the steps then pass
    private sealed class RepairingShell(string firstTree, string repairedTree) : IShell
    {
        private bool _agentRan;

        public Task<ShellResult> RunAsync(string file, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) => RunAsync(file, arguments, null!, timeout, cancellationToken);

        public Task<ShellResult> RunAsync(string file, IReadOnlyList<string> arguments, string input, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (file != "ssh")
            {
                return Task.FromResult(new ShellResult(0, string.Empty, TimedOut: false));
            }
            var script = arguments[^1];
            if (script.Contains("git clone", StringComparison.Ordinal))
            {
                return Task.FromResult(new ShellResult(0, $"HEAD {new string('a', 40)}\nTREE {firstTree}\n", TimedOut: false));
            }
            if (script.Contains("ls -1", StringComparison.Ordinal))
            {
                return Task.FromResult(new ShellResult(0, "package.json\n", TimedOut: false));
            }
            if (script.Contains("docker run --rm -i", StringComparison.Ordinal))
            {
                _agentRan = true;
                return Task.FromResult(new ShellResult(0, "I changed server.js", TimedOut: false));
            }
            if (script.Contains("-newer", StringComparison.Ordinal))
            {
                return Task.FromResult(new ShellResult(0, "server.js\n", TimedOut: false));
            }
            if (script.Contains("base64 -w0", StringComparison.Ordinal))
            {
                return Task.FromResult(new ShellResult(0, $"FILE server.js {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("fixed();"))}\nTREE {repairedTree}\n", TimedOut: false));
            }
            if (script.Contains("npm test", StringComparison.Ordinal))
            {
                return Task.FromResult(_agentRan ? new ShellResult(0, "4 passing", TimedOut: false) : new ShellResult(1, "TypeError: Missing parameter name", TimedOut: false));
            }
            return Task.FromResult(new ShellResult(0, string.Empty, TimedOut: false));
        }
    }
}
