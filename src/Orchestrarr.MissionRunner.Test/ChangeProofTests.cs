using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.MissionRunner.Test;

/// <summary>
/// The staging world for a problem that has no pull request (ADR 0048): a free agent changes the code in the machine and **pushes its own branch**; the runner asks the forge where the branch is, runs the steps of the repository on that
/// commit and hands over the branch and the commit. It reads no file and writes no diff: the control plane reads the commit on the forge.
/// </summary>
public sealed class ChangeProofTests : IDisposable
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly string Head = new('a', 40);
    private const string PushedSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly string Pushed = PushedSha;
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

    /// <summary>The act gateway of the control plane, as the runner sees it: the answers it gives, in order (the last one again once they are used up), and how many times it was asked.</summary>
    private sealed class Credentials(params PushCredentialResponse[] answers) : IPushCredentialSource
    {
        public int Asked { get; private set; }

        public int Released { get; private set; }

        public int ReleasedEarlier { get; private set; }

        public Task ReleasePushCredentialAsync(string missionId, bool keepLatest, CancellationToken cancellationToken)
        {
            if (keepLatest)
            {
                ReleasedEarlier++;
            }
            else
            {
                Released++;
            }
            return Task.CompletedTask;
        }

        public static PushCredentialResponse Given(string token, DateTimeOffset expires, params string[] permissions) => new(true, null, token, expires, permissions.Length > 0 ? permissions : ["contents:write"]);

        public Task<PushCredentialResponse> PushCredentialAsync(string missionId, CancellationToken cancellationToken)
        {
            var answer = answers[Math.Min(Asked, answers.Length - 1)];
            Asked++;
            return Task.FromResult(answer);
        }
    }

    private static Credentials Issued() => new(Credentials.Given("the-git-token", DateTimeOffset.UtcNow.AddHours(1)));

    private static ClaimedMission Mission(string? instruction = "Update lodash to 4.17.21 and keep the tests green.", string? remarks = null) =>
        new("m1", "p-1a2b3c", "shop", "Staging", "Solve the problem", "example/shop", PullRequest: null, HeadSha: null, DateTimeOffset.UtcNow.AddHours(1), [], Remarks: remarks, Instruction: instruction);

    private sealed class Machine
    {
        /// <summary>What the forge answers to the question where the branch is: nothing when the agent pushed none.</summary>
        public bool Pushes { get; set; } = true;
        public int AgentExit { get; set; }
        public int BuildExit { get; set; }
        public bool LeakTokens { get; set; }
        public string? Prompt { get; private set; }
        public string? Setup { get; private set; }
        public string? AgentInput { get; private set; }
        public List<string> Scripts { get; } = [];

        /// <summary>What the agent wrote in /out/pipeline-changes.md (null: no such file).</summary>
        public string? Justification { get; init; }

        /// <summary>The credentials placed on the machine for git's helper, in order.</summary>
        public List<string> Placed { get; } = [];

        /// <summary>When set, the agent works until it is completed: the test moves the clock meanwhile.</summary>
        public TaskCompletionSource? AgentWorks { get; init; }

        public IShell Shell()
        {
            var shell = Substitute.For<IShell>();
            shell.RunAsync("ssh", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(call => AnswerAsync(call.Arg<IReadOnlyList<string>>()[^1], null));
            shell.RunAsync("ssh", Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(call => AnswerAsync(call.Arg<IReadOnlyList<string>>()[^1], (string)call.Args()[2]));
            return shell;
        }

        private async Task<ShellResult> AnswerAsync(string script, string? input)
        {
            if (script.Contains("claude -p", StringComparison.Ordinal) && AgentWorks is { } works)
            {
                AgentInput = input;
                await works.Task;
            }
            lock (Scripts)
            {
                return Answer(script, input);
            }
        }

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
            if (script.Contains("~/agentout/pipeline-changes.md", StringComparison.Ordinal))
            {
                return new ShellResult(0, Justification ?? string.Empty, false);
            }
            if (script.Contains("cat > ~/agentin/git-token.next", StringComparison.Ordinal))
            {
                Placed.Add(input ?? string.Empty);
                return new ShellResult(0, string.Empty, false);
            }
            if (script.Contains("cat > ~/agentin/setup.sh", StringComparison.Ordinal))
            {
                Setup = input;
                return new ShellResult(0, string.Empty, false);
            }
            if (script.Contains("claude -p", StringComparison.Ordinal))
            {
                AgentInput = input;
                return new ShellResult(AgentExit, LeakTokens ? "pushed with the-git-token, renewed-token and the-token" : "I updated lodash, ran the tests and pushed.", false);
            }
            if (script.Contains("git ls-remote", StringComparison.Ordinal))
            {
                var branch = script[(script.IndexOf("refs/heads/", StringComparison.Ordinal) + 11)..].Trim();
                return new ShellResult(0, Pushes ? $"{PushedSha}\trefs/heads/{branch}\n" : string.Empty, false);
            }
            if (script.Contains("git reset -q --hard", StringComparison.Ordinal))
            {
                return new ShellResult(0, $"TREE {Tree}\nFILES 2\n", false);
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

    private async Task<(MissionHandOver Dossier, Machine Machine, List<string> Lines)> Run(Machine machine, ClaimedMission? mission = null, bool free = true, IPushCredentialSource? credentials = null, TimeProvider? time = null)
    {
        var lines = new List<string>();
        var dossier = await new ChangeProof(machine.Shell(), Options(free), credentials ?? Issued(), time).RunAsync(mission ?? Mission(), line => { lock (lines) { lines.Add(line); } return Task.CompletedTask; }, Ct);
        return (dossier, machine, lines);
    }

    [Fact]
    public async Task A_branch_that_the_agent_pushed_and_that_passes_the_steps_is_handed_over_as_a_branch_and_a_commit_and_no_file()
    {
        var (dossier, machine, _) = await Run(new Machine());

        dossier.Branch.Should().StartWith("agent/p-1a2b3c-").And.MatchRegex("^agent/[a-z0-9][a-z0-9-]{0,79}$");
        dossier.Commit.Should().Be(Pushed);
        dossier.Files.Should().BeNull("the runner reads no file: the control plane reads the commit on the forge");
        dossier.EvidenceDigests.Should().Contain($"artifact:tree={Tree}").And.Contain("repair:files=2").And.Contain(digest => digest.StartsWith("change:branch=agent/", StringComparison.Ordinal) && digest.EndsWith($"@{Pushed}", StringComparison.Ordinal));
        dossier.Claim.Should().Contain("a free agent pushed agent/").And.Contain("pass");
        dossier.Complex.Should().BeFalse();
        machine.Scripts.Should().NotContain(script => script.Contains("base64", StringComparison.Ordinal) || script.Contains("git diff --no-color", StringComparison.Ordinal), "nothing is read back and no diff is written by the machine");
    }

    [Fact]
    public async Task The_steps_run_on_the_commit_that_was_pushed_in_a_clean_tree_after_the_containers_of_the_agent_are_gone()
    {
        var machine = new Machine();

        _ = await Run(machine);

        var reset = machine.Scripts.FindIndex(script => script.Contains($"git reset -q --hard {Pushed}", StringComparison.Ordinal) && script.Contains("git clean -fdxq", StringComparison.Ordinal));
        var removed = machine.Scripts.FindIndex(script => script.Contains("docker ps -aq | xargs -r docker rm -f", StringComparison.Ordinal));
        var steps = machine.Scripts.FindIndex(script => script.Contains("npm ci", StringComparison.Ordinal) || script.Contains("docker run --rm --cap-drop", StringComparison.Ordinal));
        reset.Should().BeGreaterThan(-1);
        reset.Should().BeLessThan(removed);
        removed.Should().BeGreaterThan(-1);
        steps.Should().BeGreaterThan(removed, "the steps run after the containers are gone, and after the commit is checked out");
    }

    [Fact]
    public async Task The_agent_is_told_the_machine_the_task_of_the_lead_and_how_to_deliver_and_the_tokens_never_enter_its_task()
    {
        var (_, machine, _) = await Run(new Machine(), Mission(remarks: "Show the exact versions."));

        machine.Prompt.Should().Contain("isolated machine").And.Contain("every tool of a developer").And.Contain("`docker run` works")
            .And.Contain("Update lodash to 4.17.21").And.Contain("Show the exact versions.").And.Contain("production is built from it")
            .And.Contain("git push origin HEAD:agent/").And.Contain("pushing does not finish anything").And.Contain("do not open or merge a pull request").And.Contain(".npmrc")
            .And.Contain("the python-version or node-version of its workflow", "the pipeline may run an older runtime than staging: the change must install there too").And.Contain("Stay within the task");
        machine.Prompt.Should().NotContain("the-token").And.NotContain("the-git-token");
        machine.Setup.Should().Contain("credential.https://github.com.helper", "only the forge's host receives the credential").And.Contain("cat /in/git-token").And.NotContain("the-git-token", "git's helper reads the file that the runner keeps up to date");
        machine.AgentInput.Should().Be("the-token\n", "the subscription's token goes by the standard input of the connection, never in an argument");
        machine.Placed.Should().Equal(["the-git-token"], "the credential the control plane made is placed by the standard input of the connection");
        machine.Scripts.Should().OnlyContain(script => !script.Contains("the-token", StringComparison.Ordinal) && !script.Contains("the-git-token", StringComparison.Ordinal));
        machine.Scripts.Single(script => script.Contains("claude -p", StringComparison.Ordinal)).Should().NotContain("GIT_TOKEN").And.Contain("--dangerously-skip-permissions")
            .And.Contain("--name orchestrarr-agent", "the agent's own container has a name that it is told never to remove");
        machine.Prompt.Should().Contain("orchestrarr-agent").And.Contain("never stop, kill or remove it");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task What_the_agent_says_never_carries_a_token_into_the_dossier_whether_it_finished_or_not(int agentExit)
    {
        var (dossier, _, lines) = await Run(new Machine { LeakTokens = true, AgentExit = agentExit });

        string.Join(" ", dossier.Claim, dossier.WhatChangedAndWhy, string.Join(" ", dossier.OpenPoints ?? []), string.Join(" ", lines)).Should().NotContain("the-git-token").And.NotContain("the-token");
    }

    [Fact]
    public async Task The_image_tag_follows_the_content_of_the_dockerfile()
    {
        string Tag(Machine machine) => machine.Scripts.Single(script => script.Contains("docker build -q -t", StringComparison.Ordinal)).Split("-t ")[1].Split(' ')[0];
        var first = new Machine();
        var again = new Machine();
        var other = new Machine();

        _ = await Run(first);
        _ = await Run(again);
        var changed = Options();
        await File.WriteAllTextAsync(Path.Combine(_folder, "Dockerfile"), "FROM node:24", Ct);
        _ = await new ChangeProof(other.Shell(), changed, Issued()).RunAsync(Mission(), _ => Task.CompletedTask, Ct);

        Tag(first).Should().Be(Tag(again), "the same Dockerfile is the same image");
        Tag(other).Should().NotBe(Tag(first), "another Dockerfile is another image");
    }

    [Fact]
    public async Task An_agent_that_pushed_nothing_or_that_fails_hands_over_a_dossier_with_no_branch()
    {
        var nothing = (await Run(new Machine { Pushes = false })).Dossier;
        var failed = (await Run(new Machine { AgentExit = 2 })).Dossier;

        nothing.Branch.Should().BeNull();
        nothing.Claim.Should().Contain("pushed no branch");
        failed.Branch.Should().BeNull();
        failed.Claim.Should().Contain("did not finish");
    }

    [Fact]
    public async Task A_commit_that_the_steps_of_the_repository_do_not_pass_is_not_handed_over_as_a_branch_and_says_so()
    {
        var (dossier, _, lines) = await Run(new Machine { BuildExit = 1 });

        dossier.Branch.Should().BeNull();
        dossier.Claim.Should().Contain("does NOT pass the steps");
        lines.Should().Contain(line => line.Contains("do not pass", StringComparison.Ordinal));
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
        var routed = await new StagingProof(machine.Shell(), options, null, new ChangeProof(machine.Shell(), options, Issued())).RunAsync(Mission(), _ => Task.CompletedTask, Ct);
        var refused = await new StagingProof(new Machine().Shell(), options).RunAsync(Mission(), _ => Task.CompletedTask, Ct);

        routed.Branch.Should().NotBeNullOrEmpty();
        refused.Claim.Should().StartWith("Nothing was proven");
    }

    [Fact]
    public async Task Without_a_credential_from_the_control_plane_the_agent_never_starts_and_the_refusal_is_said()
    {
        var refused = new Credentials(new PushCredentialResponse(false, "the global stop is engaged", null, null, []));

        var (dossier, machine, _) = await Run(new Machine(), credentials: refused);

        dossier.Claim.Should().StartWith("Nothing was done").And.Contain("gave no credential").And.Contain("the global stop is engaged");
        machine.Scripts.Should().NotContain(script => script.Contains("claude -p", StringComparison.Ordinal), "an agent that cannot push is not started");
        machine.Placed.Should().BeEmpty();
    }

    [Fact]
    public async Task While_the_agent_works_its_credential_is_renewed_before_it_expires_and_taken_away_when_the_control_plane_refuses()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero));
        var credentials = new Credentials(
            Credentials.Given("the-git-token", time.GetUtcNow().AddHours(1), "contents:write", "workflows:write"),
            Credentials.Given("renewed-token", time.GetUtcNow().AddHours(2)),
            new PushCredentialResponse(false, "the approval was withdrawn", null, null, []));
        var machine = new Machine { AgentWorks = new TaskCompletionSource(), LeakTokens = true };

        var running = Run(machine, credentials: credentials, time: time);
        await Until(() => machine.AgentInput is not null);
        time.Advance(TimeSpan.FromMinutes(40));
        await Until(() => machine.Placed.Count == 2);
        time.Advance(TimeSpan.FromMinutes(40));
        await Until(() => { lock (machine.Scripts) { return machine.Scripts.Exists(script => script.EndsWith("rm -f ~/agentin/git-token", StringComparison.Ordinal)); } });
        machine.AgentWorks.SetResult();
        var (dossier, _, lines) = await running;

        machine.Placed.Should().Equal(["the-git-token", "renewed-token"], "the credential is asked for again before it expires, and the new one replaces the old");
        credentials.Asked.Should().Be(3);
        credentials.Released.Should().Be(1, "once the agent is done, the control plane is told so that it revokes the credential on the forge");
        credentials.ReleasedEarlier.Should().Be(1, "the credential that the renewed one replaces is revoked only once the renewed one is placed");
        lines.Should().Contain(line => line.Contains("took the push credential back", StringComparison.Ordinal) && line.Contains("withdrawn", StringComparison.Ordinal));
        lines.Should().Contain(line => line.Contains("contents:write, workflows:write", StringComparison.Ordinal), "what the credential may do is said");
        string.Join(" ", dossier.Claim, dossier.WhatChangedAndWhy, string.Join(" ", lines)).Should().NotContain("the-git-token").And.NotContain("renewed-token");
    }

    [Theory]
    [InlineData(60, 40, 40)]
    [InlineData(30, 40, 25)]
    [InlineData(4, 40, 0.5)]
    public void The_credential_is_asked_for_again_every_so_often_and_at_the_latest_five_minutes_before_it_expires(int expiresInMinutes, int everyMinutes, double expectedMinutes)
    {
        var now = new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);

        ChangeProof.RenewalDelay(now.AddMinutes(expiresInMinutes), now, everyMinutes).Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public async Task What_the_agent_wrote_of_its_pipeline_changes_is_handed_over_for_the_control_plane_to_check_and_never_carries_a_token()
    {
        var (dossier, machine, _) = await Run(new Machine { Justification = "p1 used the removed set-output command; pushed with the-git-token" });
        var (silent, _, _) = await Run(new Machine());

        dossier.PipelineJustification.Should().Contain("removed set-output command").And.NotContain("the-git-token");
        silent.PipelineJustification.Should().BeNull("no file, nothing said");
        machine.Prompt.Should().Contain("/out/pipeline-changes.md").And.Contain("checked against your lines");
        machine.Scripts.Single(script => script.Contains("claude -p", StringComparison.Ordinal)).Should().Contain("-v ~/agentout:/out", "the only place the agent writes outside its clone");
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }
        condition().Should().BeTrue("the runner should have got there");
    }
}
