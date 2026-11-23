using Orchestrarr.Core.Missions;

namespace Orchestrarr.MissionRunner.Test;

/// <summary>The staging world for a problem that has no pull request (ADR 0048): a free agent changes the code in the machine, and the runner reads the files back itself, applies the policy of what may leave and proves the change on its own.</summary>
public sealed class ChangeProofTests : IDisposable
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly string Head = new('a', 40);
    private static readonly string Tree = new('c', 40);
    private readonly string _folder = Directory.CreateTempSubdirectory("orchestrarr-change-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private MissionRunnerOptions Options(bool free = true)
    {
        var dockerfile = Path.Combine(_folder, "Dockerfile");
        var token = Path.Combine(_folder, "token");
        File.WriteAllText(dockerfile, "FROM node");
        File.WriteAllText(token, "the-token\n");
        return new MissionRunnerOptions { StagingMachine = "staging-1", Repositories = ["example/*"], Repair = free ? new RepairSettings { FreeDockerfileFile = dockerfile, TokenFile = token } : new RepairSettings() };
    }

    private static ClaimedMission Mission(string? instruction = "Update lodash to 4.17.21 and keep the tests green.", string? remarks = null) =>
        new("m1", "p1", "shop", "Staging", "Solve the problem", "example/shop", PullRequest: null, HeadSha: null, DateTimeOffset.UtcNow.AddHours(1), [], Remarks: remarks, Instruction: instruction);

    private sealed class Machine
    {
        public string Status { get; set; } = " M package.json\n M package-lock.json\n";
        public int AgentExit { get; set; }
        public int BuildExit { get; set; }
        public string? Prompt { get; private set; }
        public string? AgentInput { get; private set; }
        public List<string> Scripts { get; } = [];

        public IShell Shell()
        {
            var shell = Substitute.For<IShell>();
            shell.RunAsync("ssh", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(call => Answer(call.Arg<IReadOnlyList<string>>()[^1], null));
            shell.RunAsync("ssh", Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(call => Answer(call.Arg<IReadOnlyList<string>>()[^1], (string)call.Args()[2]));
            return shell;
        }

        /// <summary>A path whose content is longer than one slice of what a call may bring back.</summary>
        public string? BigPath { get; set; }

        public List<int> SliceLengths { get; } = [];

        // The file that a script of the runner names: base64 -w0 "<path>"
        private static string PathOf(string script)
        {
            var start = script.IndexOf("base64 -w0 \"", StringComparison.Ordinal) + 12;
            return script[start..script.IndexOf('"', start)];
        }

        private string Encoded(string path) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(path == BigPath ? new string('a', 200_000) : $"content of {path}"));

        private ShellResult Answer(string script, string? input)
        {
            Scripts.Add(script);
            if (script.Contains("git clone", StringComparison.Ordinal))
            {
                return new ShellResult(0, $"HEAD {Head}\nBRANCH develop\n", false);
            }
            if (script.Contains("cat > ~/agentin/prompt.txt", StringComparison.Ordinal))
            {
                Prompt = input;
                return new ShellResult(0, string.Empty, false);
            }
            if (script.Contains("claude -p", StringComparison.Ordinal))
            {
                AgentInput = input;
                return new ShellResult(AgentExit, "I updated lodash and ran the tests.", false);
            }
            if (script.Contains("git status --porcelain", StringComparison.Ordinal))
            {
                return new ShellResult(0, Status, false);
            }
            if (script.Contains("| wc -c", StringComparison.Ordinal))
            {
                return new ShellResult(0, $"{Encoded(PathOf(script)).Length}\n", false);
            }
            if (script.Contains("| cut -c", StringComparison.Ordinal))
            {
                var range = script[(script.IndexOf("| cut -c", StringComparison.Ordinal) + 8)..].Split('-');
                var from = int.Parse(range[0], System.Globalization.CultureInfo.InvariantCulture) - 1;
                var to = int.Parse(range[1], System.Globalization.CultureInfo.InvariantCulture);
                var text = Encoded(PathOf(script));
                SliceLengths.Add(Math.Min(to, text.Length) - from);
                return new ShellResult(0, text[from..Math.Min(to, text.Length)] + "\n", false);
            }
            if (script.Contains("git diff --no-color", StringComparison.Ordinal))
            {
                return new ShellResult(0, "-    \"lodash\": \"4.17.4\"\n+    \"lodash\": \"4.17.21\"\n", false);
            }
            if (script.Contains("git write-tree", StringComparison.Ordinal))
            {
                return new ShellResult(0, $"TREE {Tree}\n", false);
            }
            if (script.Contains("ls -1", StringComparison.Ordinal))
            {
                return new ShellResult(0, "package.json\npackage-lock.json\nserver.js\n", false);
            }
            if (script.Contains("docker run --rm --cap-drop", StringComparison.Ordinal) || script.Contains("npm ci", StringComparison.Ordinal))
            {
                return new ShellResult(BuildExit, BuildExit == 0 ? "4 passing" : "1 failing", false);
            }
            return new ShellResult(0, string.Empty, false);
        }
    }

    private async Task<(MissionHandOver Dossier, Machine Machine, List<string> Lines)> Run(Machine machine, ClaimedMission? mission = null, bool free = true)
    {
        var lines = new List<string>();
        var dossier = await new ChangeProof(machine.Shell(), Options(free)).RunAsync(mission ?? Mission(), line => { lines.Add(line); return Task.CompletedTask; }, Ct);
        return (dossier, machine, lines);
    }

    [Fact]
    public async Task A_file_longer_than_what_one_call_may_bring_back_is_read_in_slices_and_handed_over_whole()
    {
        var machine = new Machine { BigPath = "package-lock.json" };

        var (dossier, _, _) = await Run(machine);

        dossier.Files.Should().Contain(file => file.Path == "package-lock.json" && file.Content.Length == 200_000);
        machine.SliceLengths.Should().HaveCountGreaterThan(1).And.OnlyContain(length => length <= 120_000);
        dossier.EvidenceDigests.Should().Contain($"artifact:tree={Tree}");
    }

    [Fact]
    public async Task A_change_that_the_runner_read_back_and_proved_is_handed_over_whole_with_the_commit_it_was_made_against_and_the_tree()
    {
        var (dossier, machine, _) = await Run(new Machine());

        dossier.Files.Should().HaveCount(2).And.Contain(file => file.Path == "package.json");
        dossier.Commit.Should().Be(Head);
        dossier.EvidenceDigests.Should().Contain($"artifact:tree={Tree}").And.Contain("repair:files=2");
        dossier.Claim.Should().Contain("a free agent made a change").And.Contain("package.json");
        dossier.WhatChangedAndWhy.Should().Contain("\"lodash\": \"4.17.21\"", "the lines that changed are read by the runner, not told by the agent").And.Contain("Generated files changed too: package-lock.json");
        dossier.Complex.Should().BeFalse();
        machine.Scripts.Should().Contain(script => script.Contains("npm ci", StringComparison.Ordinal), "the runner runs the steps again itself");
    }

    [Fact]
    public async Task The_agent_is_told_what_the_machine_is_the_task_of_the_lead_and_what_must_not_leave_and_the_token_never_enters_its_task()
    {
        var (_, machine, _) = await Run(new Machine(), Mission(remarks: "the lock file was not updated"));

        machine.Prompt.Should().Contain("isolated machine").And.Contain("production is built from it").And.Contain("every tool of a developer").And.Contain("`docker run` works")
            .And.Contain("The task: Update lodash to 4.17.21").And.Contain("Do not commit and do not push").And.Contain("pipeline file")
            .And.Contain("the controller sent your previous one back with this remark: the lock file was not updated");
        machine.Prompt.Should().NotContain("the-token");
        machine.AgentInput.Should().Be("the-token\n", "the token reaches the agent by the standard input of the connection only");
        machine.Scripts.Single(script => script.Contains("claude -p", StringComparison.Ordinal)).Should().Contain("--dangerously-skip-permissions").And.Contain("docker.sock").And.NotContain("the-token");
    }

    [Theory]
    [InlineData(" M .github/workflows/ci.yml\n M package.json\n", ".github/workflows/ci.yml")]
    [InlineData("?? deploy/.env\n", "deploy/.env")]
    [InlineData(" M CLAUDE.md\n", "CLAUDE.md")]
    public async Task A_change_that_touches_a_pipeline_a_secret_or_a_file_that_instructs_an_agent_is_not_carried_and_nothing_is_handed_over(string status, string path)
    {
        var (dossier, _, _) = await Run(new Machine { Status = status });

        dossier.Files.Should().BeNull();
        dossier.Claim.Should().Contain("cannot be carried").And.Contain(path);
        dossier.EvidenceDigests.Should().Contain("change:refused");
    }

    [Fact]
    public async Task A_deleted_or_renamed_file_is_refused_because_a_change_is_carried_as_added_and_modified_files()
    {
        (await Run(new Machine { Status = " D server.js\n" })).Dossier.Claim.Should().Contain("deleted or renamed");
        (await Run(new Machine { Status = "R  a.js -> b.js\n" })).Dossier.Claim.Should().Contain("deleted or renamed");
    }

    [Fact]
    public async Task What_an_install_leaves_in_the_folder_is_not_the_change()
    {
        var (dossier, _, _) = await Run(new Machine { Status = "?? node_modules/lodash/index.js\n?? coverage/lcov.info\n M package.json\n" });

        dossier.Files.Should().ContainSingle().Which.Path.Should().Be("package.json");
    }

    [Fact]
    public async Task A_change_that_the_steps_of_the_repository_do_not_pass_is_not_handed_over_as_files_and_says_so()
    {
        var (dossier, _, _) = await Run(new Machine { BuildExit = 1 });

        dossier.Files.Should().BeNull();
        dossier.Claim.Should().Contain("does NOT pass").And.Contain("1 failing");
        dossier.OpenPoints.Should().NotBeEmpty();
    }

    [Fact]
    public async Task An_agent_that_fails_or_changes_nothing_hands_over_a_dossier_with_no_change()
    {
        var failed = (await Run(new Machine { AgentExit = 2 })).Dossier;
        var nothing = (await Run(new Machine { Status = string.Empty })).Dossier;

        failed.Files.Should().BeNull();
        failed.Claim.Should().Contain("did not finish");
        nothing.Files.Should().BeNull();
        nothing.Claim.Should().Contain("changed nothing");
    }

    [Fact]
    public async Task A_runner_with_no_free_agent_or_for_another_repository_refuses_and_does_not_touch_the_machine()
    {
        var unconfigured = await Run(new Machine(), free: false);
        var other = await Run(new Machine(), Mission() with { Repository = "elsewhere/shop" });

        unconfigured.Dossier.Claim.Should().StartWith("Nothing was done").And.Contain("no free agent");
        unconfigured.Machine.Scripts.Should().BeEmpty();
        other.Dossier.Claim.Should().Contain("does not accept");
        other.Machine.Scripts.Should().BeEmpty();
    }

    [Fact]
    public async Task The_staging_proof_gives_a_mission_with_no_pull_request_to_the_change_proof_and_still_refuses_it_without_one()
    {
        var machine = new Machine();
        var options = Options();
        var routed = await new StagingProof(machine.Shell(), options, null, new ChangeProof(machine.Shell(), options)).RunAsync(Mission(), _ => Task.CompletedTask, Ct);
        var refused = await new StagingProof(new Machine().Shell(), options).RunAsync(Mission(), _ => Task.CompletedTask, Ct);

        routed.Files.Should().NotBeNullOrEmpty();
        refused.Claim.Should().StartWith("Nothing was proven");
    }
}
