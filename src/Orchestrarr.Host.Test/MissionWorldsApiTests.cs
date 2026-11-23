using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

/// <summary>The three worlds of a problem through the control plane: the staging proof, the merge by the act gateway, the deployment, each crossing decided by the controller on the facts of the forge. The runner is played by the test.</summary>
public sealed class MissionWorldsApiTests
{
    private static readonly string Token = new('w', 48);
    private static readonly string Head = new('d', 40);
    private static readonly string MergeCommit = new('e', 40);
    private static readonly string Tree = new('7', 40);
    private static readonly string AgentSha = new('a', 40);
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly object Hello = new { hello = new { name = "runner-staging", version = "1", kinds = new[] { "validatePullRequest" }, repositories = new[] { "example/*" }, delegations = Array.Empty<object>(), secondFactorRequired = false } };

    /// <summary>A forge in memory: a pull request that is green at its head, merged on demand, whose merge result has a tree and a pipeline of its own.</summary>
    private sealed class FakeForge : IPullRequestFacts, IForgeActs, IDefaultBranchSource, IBranchSource
    {
        /// <summary>Where the branch of the agent is on the forge now (null: there is none).</summary>
        public string? BranchHead { get; set; } = AgentSha;

        /// <summary>What the forge says the branch holds on top of the default branch.</summary>
        public BranchChange? Change { get; set; } = new(Head, 0, 1, [new BranchFile("package.json", "modified", 2, "@@ -1 +1 @@\n-a\n+b")], true);

        public Task<string?> HeadOfAsync(string repositoryFullName, string branch, CancellationToken cancellationToken) => Task.FromResult(BranchHead);

        public Task<BranchChange?> CompareAsync(string repositoryFullName, string baseBranch, string headSha, CancellationToken cancellationToken) => Task.FromResult(Change);

        public Task<OpenedPullRequest> OpenPullRequestFromBranchAsync(string repositoryFullName, string baseBranch, string branch, string expectedSha, string title, string body, CancellationToken cancellationToken)
        {
            if (!string.Equals(BranchHead, expectedSha, StringComparison.Ordinal))
            {
                return Task.FromResult(new OpenedPullRequest(false, 0, null, "the branch is not at the commit that was audited"));
            }
            Opened.Add((branch, expectedSha, []));
            Texts.Add((title, body));
            PullHead = expectedSha;
            return Task.FromResult(new OpenedPullRequest(true, RepairNumber, expectedSha, null));
        }

        /// <summary>The head of the default branch, as the forge says it now.</summary>
        public string DefaultHead { get; set; } = Head;

        /// <summary>False: the forge cannot say what the default branch is.</summary>
        public bool DefaultReadable { get; set; } = true;

        /// <summary>The title and the body of each pull request that the gateway opened.</summary>
        public List<(string Title, string Body)> Texts { get; } = [];

        /// <summary>The head of the pull request that the gateway opened: a repair's commit, or the commit of the branch of an agent.</summary>
        public string PullHead { get; private set; } = RepairHead;

        public bool Merged { get; private set; }

        public ChecksState HeadChecks { get; set; } = ChecksState.Passing;

        public ChecksState MergeChecks { get; set; } = ChecksState.Passing;

        public string TreeOfTheMerge { get; set; } = Tree;

        public string BaseRef { get; set; } = "main";

        public Task<PullRequestFacts?> ReadAsync(string repositoryFullName, int number, CancellationToken cancellationToken) =>
            Task.FromResult<PullRequestFacts?>(new PullRequestFacts(Opened.Count > 0 && number == RepairNumber ? PullHead : Head, "base", false, Opened.Count > 0 && number == RepairNumber ? ChecksState.Passing : HeadChecks, ["package.json"], PathsComplete: true, Merged, Merged ? MergeCommit : null, Merged ? TreeOfTheMerge : null, Merged ? MergeChecks : ChecksState.Unknown, BaseRef: BaseRef, DefaultBranch: "main"));

        public Task<DefaultBranchHead?> ReadAsync(string repositoryFullName, CancellationToken cancellationToken) =>
            Task.FromResult<DefaultBranchHead?>(DefaultReadable ? new DefaultBranchHead("main", DefaultHead) : null);

        public List<(string Branch, string Parent, IReadOnlyList<PatchedFile> Files)> Opened { get; } = [];

        public List<int> Closed { get; } = [];

        /// <summary>The pull request that carries a repair, once opened: the merge and the reads are about it.</summary>
        public const int RepairNumber = 42;

        public static readonly string RepairHead = new('f', 40);

        public Task<OpenedPullRequest> OpenPullRequestAsync(string repositoryFullName, string baseBranch, string parentSha, string branch, IReadOnlyList<PatchedFile> files, string title, string body, CancellationToken cancellationToken)
        {
            Opened.Add((branch, parentSha, files));
            Texts.Add((title, body));
            return Task.FromResult(new OpenedPullRequest(true, RepairNumber, RepairHead, null));
        }

        public Task<bool> ClosePullRequestAsync(string repositoryFullName, int number, string comment, CancellationToken cancellationToken)
        {
            Closed.Add(number);
            return Task.FromResult(true);
        }

        public Task<MergeResult> MergePullRequestAsync(string repositoryFullName, int number, string expectedHeadSha, CancellationToken cancellationToken)
        {
            if (expectedHeadSha != (Opened.Count > 0 ? PullHead : Head))
            {
                return Task.FromResult(new MergeResult(false, null, "409: the head moved"));
            }
            Merged = true;
            return Task.FromResult(new MergeResult(true, MergeCommit, null));
        }
    }

    /// <summary>Answers the decisions in turn, the last one for ever: a controller that sends a mission back once and then grants.</summary>
    private sealed class SaysGateway(params string[] decisions) : IAgentGateway
    {
        private int _asked;

        public Task<AgentReply> RunAsync(AgentRequest request, CancellationToken cancellationToken)
        {
            var decision = decisions.Length == 0 ? "grant" : decisions[Math.Min(Interlocked.Increment(ref _asked) - 1, decisions.Length - 1)];
            return Task.FromResult(new AgentReply(true, decision == "grant" ? """{"decision":"grant","reasons":["the evidence supports the claim"],"remarks":""}""" : """{"decision":"send_back","reasons":["the pipeline is not shown"],"remarks":"show the pipeline of the merge commit"}""", null));
        }
    }

    private sealed class WorldsFactory(FakeForge forge, params string[] decisions) : AccountsApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentGateway>(new SaysGateway(decisions));
                services.AddSingleton<IPullRequestFacts>(forge);
                services.AddSingleton<IForgeActs>(forge);
                services.AddSingleton<IDefaultBranchSource>(forge);
                services.AddSingleton<IBranchSource>(forge);
            });
            builder.UseEnvironment("Development");
            builder.UseSetting("Missions:Sandbox", "true");
            builder.UseSetting("Missions:SandboxMaxWorld", "Release");
            builder.UseSetting("Missions:ForbiddenPaths:0", ".github/**");
            builder.UseSetting("Missions:ForbiddenPaths:1", "infra/prod/**");
            builder.UseSetting("Missions:StagingRunner", "runner-staging");
            builder.UseSetting("Missions:GitRunner", "runner-staging");
            builder.UseSetting("Missions:ReleaseRunner", "runner-staging");
            builder.UseSetting("Tasks:Runners:0:Name", "runner-staging");
            builder.UseSetting("Tasks:Runners:0:Token", Token);
        }
    }

    private static HttpClient Runner(AccountsApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    private static async Task<string> StartProblemAsync(AccountsApiFactory factory, string problemId = "p-worlds")
    {
        var approvals = factory.Services.GetRequiredService<SandboxApprovals>();
        var entry = new PlanEntry(problemId, "example/shop", "dependency-update", "Prove pull request #7", ["example/shop"], World.Release, new MissionBudget(30, 3, 50), new Validation("alice", null), DateTimeOffset.UtcNow, 7, Head);
        (await approvals.PrepareAsync(entry, Ct)).Should().BeTrue();
        var started = await factory.Services.GetRequiredService<ProblemConductor>().StartAsync(entry, StagingDeclaration.DeclaredStaging, StagingRequirement.Dependencies, StagingState.BaselineProven, Ct);
        started.Ok.Should().BeTrue(started.Refusal);
        return problemId;
    }

    /// <summary>A problem that is not a pull request (an advisory on a dependency): no number, no head, a change to make from the default branch.</summary>
    private static async Task<string> StartChangeProblemAsync(AccountsApiFactory factory, string problemId = "p-worlds")
    {
        var approvals = factory.Services.GetRequiredService<SandboxApprovals>();
        var entry = new PlanEntry(problemId, "example/shop", "dependency-update", "Fix the advisory of a dependency", ["example/shop"], World.Release, new MissionBudget(30, 3, 50), new Validation("alice", null), DateTimeOffset.UtcNow, null, null);
        (await approvals.PrepareAsync(entry, Ct)).Should().BeTrue();
        var started = await factory.Services.GetRequiredService<ProblemConductor>().StartAsync(entry, StagingDeclaration.DeclaredStaging, StagingRequirement.Dependencies, StagingState.BaselineProven, Ct);
        started.Ok.Should().BeTrue(started.Refusal);
        return problemId;
    }

    private static async Task<JsonElement> ClaimAsync(HttpClient runner, string world)
    {
        var claim = await (await runner.PostAsJsonAsync($"/api/v1/runner/missions/claim?world={world}", Hello, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        return claim.GetProperty("mission");
    }

    private static async Task HandOverAsync(HttpClient runner, string missionId, string? commit, params string[] evidence)
    {
        (await runner.PostAsync($"/api/v1/runner/missions/{missionId}/started", null, Ct)).EnsureSuccessStatusCode();
        (await runner.PostAsJsonAsync($"/api/v1/runner/missions/{missionId}/handover", new { claim = "done", whatChangedAndWhy = "as asked", evidenceDigests = evidence, openPoints = Array.Empty<string>(), complex = false, commit }, Ct)).EnsureSuccessStatusCode();
    }

    private static async Task<string> NextMissionAsync(AccountsApiFactory factory, HttpClient runner, string world)
    {
        var driver = factory.Services.GetServices<IHostedService>().OfType<MissionDriver>().Single();
        // The driver and the claim answer at once: a mission that is not offered after a pass is not coming
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await driver.PassAsync(Ct);
            var mission = await ClaimAsync(runner, world);
            if (mission.ValueKind == JsonValueKind.Object)
            {
                return mission.GetProperty("missionId").GetString()!;
            }
        }
        throw new InvalidOperationException($"No mission of the {world} world was offered.");
    }

    [Fact]
    public async Task A_pull_request_goes_from_the_staging_proof_to_a_merge_to_a_deployment_and_the_problem_is_closed()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);

        // The staging world: the proof, with the tree that it built
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;
        await HandOverAsync(runner, staging, Head, $"artifact:tree={Tree}");

        // The Git world: the merge is asked of the gateway, which does it; the pipeline of the merge is read by the control plane
        var git = await NextMissionAsync(factory, runner, "Git");
        (await runner.PostAsync($"/api/v1/runner/missions/{git}/started", null, Ct)).EnsureSuccessStatusCode();
        var merge = await (await runner.PostAsync($"/api/v1/runner/missions/{git}/acts/merge", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        merge.GetProperty("allowed").GetBoolean().Should().BeTrue(merge.ToString());
        merge.GetProperty("commit").GetString().Should().Be(MergeCommit);
        forge.Merged.Should().BeTrue();
        (await runner.GetFromJsonAsync<JsonElement>($"/api/v1/runner/missions/{git}/pipeline", Ct)).GetProperty("state").GetString().Should().Be("passing");
        (await runner.PostAsJsonAsync($"/api/v1/runner/missions/{git}/handover", new { claim = "merged", whatChangedAndWhy = "squash", evidenceDigests = new[] { "pipeline:passing" }, openPoints = Array.Empty<string>(), complex = false, commit = MergeCommit }, Ct)).EnsureSuccessStatusCode();

        // The release world: the gateway allows the deployment of the commit that the controller accepted, nothing else
        var release = await NextMissionAsync(factory, runner, "Release");
        (await ClaimAsyncById(factory, release)).Should().Be(MergeCommit, "the release is offered the commit that the controller accepted");
        (await runner.PostAsync($"/api/v1/runner/missions/{release}/started", null, Ct)).EnsureSuccessStatusCode();
        var deploy = await (await runner.PostAsync($"/api/v1/runner/missions/{release}/acts/deploy", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        deploy.GetProperty("allowed").GetBoolean().Should().BeTrue(deploy.ToString());
        deploy.GetProperty("commit").GetString().Should().Be(MergeCommit);
        (await runner.PostAsJsonAsync($"/api/v1/runner/missions/{release}/handover", new { claim = "deployed and verified", whatChangedAndWhy = "container", evidenceDigests = new[] { "verify:ok" }, openPoints = Array.Empty<string>(), complex = false, commit = MergeCommit }, Ct)).EnsureSuccessStatusCode();
        var driver = factory.Services.GetServices<IHostedService>().OfType<MissionDriver>().Single();
        await driver.PassAsync(Ct);

        factory.Services.GetRequiredService<IProblemStore>().Get("p-worlds")!.State.Should().Be(ProblemState.Closed);
        factory.Services.GetRequiredService<IMissionStore>().ForProblem("p-worlds").Select(mission => mission.World).Should().Equal(World.Staging, World.Git, World.Release);
    }

    private static Task<string?> ClaimAsyncById(AccountsApiFactory factory, string missionId) =>
        Task.FromResult(factory.Services.GetRequiredService<ProblemConductor>().AcceptedCommit(missionId));

    [Fact]
    public async Task A_mission_that_the_controller_sends_back_is_offered_once_more_with_its_remarks_and_goes_on_with_the_same_mandate()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge, "send_back", "grant");
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;
        (await runner.PostAsync($"/api/v1/runner/missions/{staging}/started", null, Ct)).EnsureSuccessStatusCode();
        var dossier = new { claim = "done", whatChangedAndWhy = "as asked", evidenceDigests = new[] { $"artifact:tree={Tree}" }, openPoints = Array.Empty<string>(), complex = false, commit = Head };
        (await runner.PostAsJsonAsync($"/api/v1/runner/missions/{staging}/handover", dossier, Ct)).EnsureSuccessStatusCode();
        var driver = factory.Services.GetServices<IHostedService>().OfType<MissionDriver>().Single();

        await driver.PassAsync(Ct);
        var again = await ClaimAsync(runner, "Staging");

        again.GetProperty("missionId").GetString().Should().Be(staging, "the same mission goes on");
        again.GetProperty("rework").GetBoolean().Should().BeTrue();
        again.GetProperty("remarks").GetString().Should().Contain("pipeline of the merge commit");
        (await ClaimAsync(runner, "Staging")).ValueKind.Should().Be(JsonValueKind.Null, "it is offered once for each send-back");
        (await runner.PostAsJsonAsync($"/api/v1/runner/missions/{staging}/handover", dossier, Ct)).EnsureSuccessStatusCode();
        factory.Services.GetRequiredService<IMissionStore>().Get(staging)!.SendBacks.Should().Be(1);
    }

    [Fact]
    public async Task The_merge_and_the_authorisation_are_both_in_the_audit_trail()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        await HandOverAsync(runner, (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!, Head, $"artifact:tree={Tree}");
        var git = await NextMissionAsync(factory, runner, "Git");
        (await runner.PostAsync($"/api/v1/runner/missions/{git}/started", null, Ct)).EnsureSuccessStatusCode();

        (await runner.PostAsync($"/api/v1/runner/missions/{git}/acts/merge", null, Ct)).EnsureSuccessStatusCode();

        factory.Services.GetRequiredService<Orchestrarr.Core.Approvals.IAuditLog>().Page(100).Select(row => $"{row.Entry.Event} {row.Entry.Detail}").Should()
            .Contain(line => line.StartsWith("act.authorised", StringComparison.Ordinal))
            .And.Contain(line => line.StartsWith("act.performed", StringComparison.Ordinal) && line.Contains(MergeCommit, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sent_back_mission_whose_rework_was_handed_out_does_not_hide_the_next_mission_of_its_world()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge, "send_back", "grant");
        _ = await StartProblemAsync(factory, "p-one");
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;
        await HandOverAsync(runner, staging, Head, $"artifact:tree={Tree}");
        var driver = factory.Services.GetServices<IHostedService>().OfType<MissionDriver>().Single();
        await driver.PassAsync(Ct);
        (await ClaimAsync(runner, "Staging")).GetProperty("rework").GetBoolean().Should().BeTrue();

        // The rework is out and the runner is gone: a later mission of the same world is still offered (another repository, so its turn has come)
        var approvals = factory.Services.GetRequiredService<SandboxApprovals>();
        var other = new PlanEntry("p-two", "example/other", "dependency-update", "Prove pull request #3", ["example/other"], World.Release, new MissionBudget(30, 3, 50), new Validation("alice", null), DateTimeOffset.UtcNow, 3, Head);
        (await approvals.PrepareAsync(other, Ct)).Should().BeTrue();
        (await factory.Services.GetRequiredService<ProblemConductor>().StartAsync(other, StagingDeclaration.DeclaredStaging, StagingRequirement.Dependencies, StagingState.BaselineProven, Ct)).Ok.Should().BeTrue();

        (await ClaimAsync(runner, "Staging")).GetProperty("problemId").GetString().Should().Be("p-two");
    }

    [Fact]
    public async Task A_merge_is_refused_on_a_repository_that_the_settings_do_not_name()
    {
        var inner = Substitute.For<IForgeActs>();
        var guarded = new GuardedForgeActs(inner, ["example/*"]);

        var refused = await guarded.MergePullRequestAsync("other/shop", 7, Head, Ct);
        _ = await guarded.MergePullRequestAsync("example/shop", 7, Head, Ct);

        refused.Merged.Should().BeFalse();
        refused.Error.Should().Contain("SandboxMergeRepositories");
        await inner.Received(1).MergePullRequestAsync("example/shop", 7, Head, Ct);
        await inner.DidNotReceive().MergePullRequestAsync("other/shop", Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_merge_with_no_token_of_its_own_merges_nothing()
    {
        var result = await new NoForgeActs().MergePullRequestAsync("example/shop", 7, Head, Ct);

        result.Merged.Should().BeFalse();
    }

    [Fact]
    public async Task A_merge_waits_for_the_pipeline_of_the_pull_request_and_is_refused_when_it_is_red()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        await HandOverAsync(runner, (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!, Head, $"artifact:tree={Tree}");
        var git = await NextMissionAsync(factory, runner, "Git");
        (await runner.PostAsync($"/api/v1/runner/missions/{git}/started", null, Ct)).EnsureSuccessStatusCode();
        // The pipeline was green when the mandate was issued; it runs again (a new push to the branch) before the merge is asked
        forge.HeadChecks = ChecksState.Pending;

        var pending = await (await runner.PostAsync($"/api/v1/runner/missions/{git}/acts/merge", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        forge.HeadChecks = ChecksState.Failing;
        var red = await (await runner.PostAsync($"/api/v1/runner/missions/{git}/acts/merge", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        pending.GetProperty("allowed").GetBoolean().Should().BeFalse();
        pending.GetProperty("retry").GetBoolean().Should().BeTrue("only the pipeline is standing in the way, and it is not over");
        red.GetProperty("allowed").GetBoolean().Should().BeFalse();
        red.GetProperty("retry").GetBoolean().Should().BeFalse("a red pipeline is final");
        forge.Merged.Should().BeFalse("nothing is merged while a check of the code fails");
    }

    [Fact]
    public async Task A_release_of_a_merge_that_is_not_the_tree_proved_in_staging_is_not_deployed()
    {
        var forge = new FakeForge { TreeOfTheMerge = new string('9', 40) };
        using var factory = new WorldsFactory(forge);
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        await HandOverAsync(runner, (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!, Head, $"artifact:tree={Tree}");
        var git = await NextMissionAsync(factory, runner, "Git");
        (await runner.PostAsync($"/api/v1/runner/missions/{git}/started", null, Ct)).EnsureSuccessStatusCode();
        (await runner.PostAsync($"/api/v1/runner/missions/{git}/acts/merge", null, Ct)).EnsureSuccessStatusCode();
        (await runner.PostAsJsonAsync($"/api/v1/runner/missions/{git}/handover", new { claim = "merged", whatChangedAndWhy = "squash", evidenceDigests = new[] { "pipeline:passing" }, openPoints = Array.Empty<string>(), complex = false, commit = MergeCommit }, Ct)).EnsureSuccessStatusCode();
        var driver = factory.Services.GetServices<IHostedService>().OfType<MissionDriver>().Single();

        await driver.PassAsync(Ct);

        factory.Services.GetRequiredService<IMissionStore>().ForProblem("p-worlds").Select(mission => mission.World).Should().NotContain(World.Release, "the base moved between the proof and the merge: another artifact, no release");
    }

    [Fact]
    public async Task A_pull_request_whose_base_is_not_the_default_branch_is_not_proved_nor_merged()
    {
        var forge = new FakeForge { BaseRef = "release" };
        using var factory = new WorldsFactory(forge);
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;
        await HandOverAsync(runner, staging, Head, $"artifact:tree={Tree}");
        var driver = factory.Services.GetServices<IHostedService>().OfType<MissionDriver>().Single();

        await driver.PassAsync(Ct);

        factory.Services.GetRequiredService<IMissionStore>().ForProblem("p-worlds").Select(mission => mission.World).Should().Equal(World.Staging);
        forge.Merged.Should().BeFalse();
    }

    private static async Task<HttpResponseMessage> HandOverBranchAsync(HttpClient runner, string missionId, string? commit = null, bool start = true, string branch = "agent/p-worlds-1a2b3c")
    {
        if (start)
        {
            (await runner.PostAsync($"/api/v1/runner/missions/{missionId}/started", null, Ct)).EnsureSuccessStatusCode();
        }
        return await runner.PostAsJsonAsync($"/api/v1/runner/missions/{missionId}/handover",
            new { claim = "pushed", whatChangedAndWhy = "a branch", evidenceDigests = new[] { $"artifact:tree={Tree}", "repair:files=1" }, openPoints = Array.Empty<string>(), complex = false, commit = commit ?? AgentSha, branch }, Ct);
    }

    private static async Task<HttpResponseMessage> HandOverWithFilesAsync(HttpClient runner, string missionId, object[] files, bool start = true)
    {
        if (start)
        {
            (await runner.PostAsync($"/api/v1/runner/missions/{missionId}/started", null, Ct)).EnsureSuccessStatusCode();
        }
        return await runner.PostAsJsonAsync($"/api/v1/runner/missions/{missionId}/handover",
            new { claim = "repaired", whatChangedAndWhy = "the source", evidenceDigests = new[] { $"artifact:tree={Tree}", "repair:files=1" }, openPoints = Array.Empty<string>(), complex = false, commit = Head, files }, Ct);
    }

    [Fact]
    public async Task A_repair_is_carried_by_a_pull_request_that_the_gateway_opens_and_that_replaces_the_original()
    {
        var forge = new FakeForge { HeadChecks = ChecksState.Failing };
        using var factory = new WorldsFactory(forge);
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;

        (await HandOverWithFilesAsync(runner, staging, [new { path = "server.js", content = "fixed" }])).EnsureSuccessStatusCode();
        var git = await NextMissionAsync(factory, runner, "Git");
        (await runner.PostAsync($"/api/v1/runner/missions/{git}/started", null, Ct)).EnsureSuccessStatusCode();

        var opened = await (await runner.PostAsync($"/api/v1/runner/missions/{git}/acts/open-pull-request", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        opened.GetProperty("opened").GetBoolean().Should().BeTrue(opened.ToString());
        opened.GetProperty("number").GetInt32().Should().Be(FakeForge.RepairNumber);
        forge.Opened.Should().ContainSingle().Which.Should().Match<(string Branch, string Parent, IReadOnlyList<PatchedFile> Files)>(made => made.Parent == Head && made.Branch.StartsWith("orchestrarr/p-worlds-", StringComparison.Ordinal) && made.Files.Single().Path == "server.js");
        var merge = await (await runner.PostAsync($"/api/v1/runner/missions/{git}/acts/merge", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        merge.GetProperty("allowed").GetBoolean().Should().BeTrue(merge.ToString());
        forge.Merged.Should().BeTrue();
        forge.Closed.Should().Equal(7);
        (await runner.GetFromJsonAsync<JsonElement>($"/api/v1/runner/missions/{git}/pipeline", Ct)).GetProperty("commit").GetString().Should().Be(MergeCommit);
        (await runner.PostAsync($"/api/v1/runner/missions/{git}/acts/open-pull-request", null, Ct)).StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        forge.Opened.Should().ContainSingle("asking again does not open a second one");
    }

    private static async Task<(string Git, HttpClient Runner)> FinishedBranchAsync(AccountsApiFactory factory, FakeForge forge)
    {
        _ = await StartChangeProblemAsync(factory);
        var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;
        (await HandOverBranchAsync(runner, staging)).EnsureSuccessStatusCode();
        var git = await NextMissionAsync(factory, runner, "Git");
        (await runner.PostAsync($"/api/v1/runner/missions/{git}/started", null, Ct)).EnsureSuccessStatusCode();
        forge.Opened.Should().BeEmpty("pushing a branch opens nothing");
        return (git, runner);
    }

    [Fact]
    public async Task A_change_with_no_pull_request_is_a_branch_that_the_agent_pushed_and_the_pull_request_is_opened_from_it_at_the_commit_that_was_read()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        var (git, runner) = await FinishedBranchAsync(factory, forge);
        using var _ = runner;

        var opened = await (await runner.PostAsync($"/api/v1/runner/missions/{git}/acts/open-pull-request", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        opened.GetProperty("opened").GetBoolean().Should().BeTrue(opened.ToString());
        forge.Opened.Should().ContainSingle().Which.Should().Match<(string Branch, string Parent, IReadOnlyList<PatchedFile> Files)>(made => made.Branch == "agent/p-worlds-1a2b3c" && made.Parent == AgentSha && made.Files.Count == 0);
        forge.Texts.Single().Title.Should().Be("fix: dependency update of example/shop", "the class of the problem is made into words");
        forge.Closed.Should().BeEmpty("there is no pull request to replace");
        var merge = await (await runner.PostAsync($"/api/v1/runner/missions/{git}/acts/merge", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        merge.GetProperty("allowed").GetBoolean().Should().BeTrue(merge.ToString());
        forge.Merged.Should().BeTrue("the pull request that carries the audited commit is merged on that commit");
    }

    [Fact]
    public async Task What_the_controller_reads_of_a_change_is_what_the_forge_says_of_the_branch()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        var (_, runner) = await FinishedBranchAsync(factory, forge);
        using var _runner = runner;

        var pending = factory.Services.GetRequiredService<RepairRegistry>().PendingOf("p-worlds")!;

        pending.Branch!.Reading.Should().Contain("package.json (modified, 2 lines)").And.Contain("+b");
        pending.Branch.Sha.Should().Be(AgentSha);
        pending.OriginalHead.Should().Be(Head, "the commit the agent started from is the common commit that the forge reports");
    }

    [Fact]
    public async Task A_branch_that_moved_after_the_agent_said_it_had_finished_is_not_opened()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        var (git, runner) = await FinishedBranchAsync(factory, forge);
        using var _ = runner;
        forge.BranchHead = new string('c', 40);

        var opened = await (await runner.PostAsync($"/api/v1/runner/missions/{git}/acts/open-pull-request", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        opened.GetProperty("opened").GetBoolean().Should().BeFalse("the commit that was audited is not the commit of the branch now");
        forge.Opened.Should().BeEmpty();
    }

    [Fact]
    public async Task A_change_with_no_pull_request_is_not_opened_when_the_default_branch_moved_since_it_was_made()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        var (git, runner) = await FinishedBranchAsync(factory, forge);
        using var _ = runner;
        forge.DefaultHead = new string('9', 40);

        var opened = await (await runner.PostAsync($"/api/v1/runner/missions/{git}/acts/open-pull-request", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        opened.GetProperty("opened").GetBoolean().Should().BeFalse();
        forge.Opened.Should().BeEmpty();
    }

    [Fact]
    public async Task A_change_with_no_pull_request_is_not_opened_when_the_forge_cannot_say_what_the_default_branch_is()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        var (git, runner) = await FinishedBranchAsync(factory, forge);
        using var _ = runner;
        forge.DefaultReadable = false;

        var opened = await (await runner.PostAsync($"/api/v1/runner/missions/{git}/acts/open-pull-request", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        opened.GetProperty("opened").GetBoolean().Should().BeFalse("nothing is written on a head that was not read");
        forge.Opened.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, "agent/p-worlds-1a2b3c")]
    [InlineData("not-a-commit", "agent/p-worlds-1a2b3c")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "master")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "agent/../x")]
    public async Task A_branch_or_a_commit_that_is_not_well_formed_is_refused_at_the_handover(string? commit, string branch)
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartChangeProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;
        (await runner.PostAsync($"/api/v1/runner/missions/{staging}/started", null, Ct)).EnsureSuccessStatusCode();

        var refused = await runner.PostAsJsonAsync($"/api/v1/runner/missions/{staging}/handover",
            new { claim = "made", whatChangedAndWhy = "the source", evidenceDigests = new[] { $"artifact:tree={Tree}" }, openPoints = Array.Empty<string>(), complex = false, commit, branch }, Ct);

        refused.StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
        factory.Services.GetRequiredService<RepairRegistry>().PendingOf("p-worlds").Should().BeNull();
    }

    private static string DossierOf(AccountsApiFactory factory, string missionId) => factory.Services.GetRequiredService<IDossierStore>().Get(missionId)!.WhatChangedAndWhy;

    [Fact]
    public async Task A_branch_that_is_not_where_the_runner_said_or_that_the_forge_cannot_read_is_kept_as_a_dossier_with_its_reason_and_no_branch()
    {
        var forge = new FakeForge { BranchHead = new string('c', 40) };
        using var factory = new WorldsFactory(forge);
        _ = await StartChangeProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;

        (await HandOverBranchAsync(runner, staging)).EnsureSuccessStatusCode();

        DossierOf(factory, staging).Should().Contain("The control plane did not accept the branch").And.Contain("not at the commit");
        factory.Services.GetRequiredService<RepairRegistry>().PendingOf("p-worlds").Should().BeNull("nothing is kept of a branch that was not accepted");
    }

    [Fact]
    public async Task A_change_that_the_forge_cannot_show_is_not_lost_either()
    {
        var forge = new FakeForge { Change = null };
        using var factory = new WorldsFactory(forge);
        _ = await StartChangeProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;

        (await HandOverBranchAsync(runner, staging)).EnsureSuccessStatusCode();

        DossierOf(factory, staging).Should().Contain("cannot be read on the forge");
        factory.Services.GetRequiredService<RepairRegistry>().PendingOf("p-worlds").Should().BeNull();
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".env")]
    [InlineData("CLAUDE.md")]
    [InlineData(".npmrc")]
    public async Task A_branch_that_changes_what_a_change_may_never_touch_is_not_integrated_and_the_reason_is_in_the_dossier_the_controller_reads(string path)
    {
        var forge = new FakeForge { Change = new BranchChange(Head, 0, 1, [new BranchFile(path, "added", 1, "@@\n+x")], true) };
        using var factory = new WorldsFactory(forge);
        _ = await StartChangeProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;

        (await HandOverBranchAsync(runner, staging)).EnsureSuccessStatusCode();

        DossierOf(factory, staging).Should().Contain("The control plane did not accept the branch").And.MatchRegex("protected|forbidden");
        factory.Services.GetRequiredService<RepairRegistry>().PendingOf("p-worlds").Should().BeNull();
    }

    [Fact]
    public async Task The_branch_of_another_problem_is_not_audited_for_this_one()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartChangeProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;

        (await HandOverBranchAsync(runner, staging, branch: "agent/p-other-1a2b3c")).StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
        factory.Services.GetRequiredService<RepairRegistry>().PendingOf("p-worlds").Should().BeNull();
    }

    [Fact]
    public async Task Files_are_handed_over_only_to_repair_a_pull_request()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartChangeProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;

        (await HandOverWithFilesAsync(runner, staging, [new { path = "package.json", content = "{}" }])).StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_repair_that_is_not_in_a_pull_request_is_not_merged_around_and_one_that_was_lost_goes_back()
    {
        var forge = new FakeForge { HeadChecks = ChecksState.Failing };
        using var factory = new WorldsFactory(forge);
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;
        (await HandOverWithFilesAsync(runner, staging, [new { path = "server.js", content = "fixed" }])).EnsureSuccessStatusCode();
        var git = await NextMissionAsync(factory, runner, "Git");
        (await runner.PostAsync($"/api/v1/runner/missions/{git}/started", null, Ct)).EnsureSuccessStatusCode();

        // Asked to merge before the pull request of the repair exists: nothing is merged, whatever the pipeline of the original says
        var early = await (await runner.PostAsync($"/api/v1/runner/missions/{git}/acts/merge", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        early.GetProperty("allowed").GetBoolean().Should().BeFalse();
        early.GetProperty("refusal").GetString().Should().Contain("not in a pull request");
        forge.Merged.Should().BeFalse();

        // The control plane forgets what it held of the repair: the staging dossier still says that one was made, so it is not replaced by the pull request that did not pass
        factory.Services.GetRequiredService<RepairRegistry>().Forget("p-worlds");
        var lost = await (await runner.PostAsync($"/api/v1/runner/missions/{git}/acts/open-pull-request", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        lost.GetProperty("opened").GetBoolean().Should().BeFalse();
        lost.GetProperty("refusal").GetString().Should().Contain("no longer held");
        forge.Opened.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_is_opened_for_a_change_that_was_not_repaired()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        await HandOverAsync(runner, (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!, Head, $"artifact:tree={Tree}");
        var git = await NextMissionAsync(factory, runner, "Git");
        (await runner.PostAsync($"/api/v1/runner/missions/{git}/started", null, Ct)).EnsureSuccessStatusCode();

        var opened = await (await runner.PostAsync($"/api/v1/runner/missions/{git}/acts/open-pull-request", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        opened.GetProperty("opened").GetBoolean().Should().BeFalse();
        opened.GetProperty("refusal").ValueKind.Should().Be(JsonValueKind.Null);
        forge.Opened.Should().BeEmpty();
    }

    [Theory]
    [InlineData("package.json")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData("test/api.test.js")]
    [InlineData("../server.js")]
    public async Task A_repair_that_touches_what_a_repair_may_not_is_refused_whatever_the_runner_checked(string path)
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;

        var response = await HandOverWithFilesAsync(runner, staging, [new { path, content = "x" }]);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.UnprocessableEntity);
        factory.Services.GetRequiredService<RepairRegistry>().PendingOf("p-worlds").Should().BeNull("nothing of a refused repair is kept");
    }

    [Fact]
    public async Task A_repair_under_a_path_that_the_certificate_forbids_is_refused_even_if_the_policy_lets_it_through()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;

        var response = await HandOverWithFilesAsync(runner, staging, [new { path = "infra/prod/deploy.js", content = "x" }]);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("frame of the mission");
    }

    [Fact]
    public async Task The_close_of_the_original_is_recorded_and_a_restart_does_not_let_the_original_be_merged_in_place_of_the_repair()
    {
        var forge = new FakeForge { HeadChecks = ChecksState.Failing };
        using var factory = new WorldsFactory(forge);
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;
        (await HandOverWithFilesAsync(runner, staging, [new { path = "server.js", content = "fixed" }])).EnsureSuccessStatusCode();
        var git = await NextMissionAsync(factory, runner, "Git");
        (await runner.PostAsync($"/api/v1/runner/missions/{git}/started", null, Ct)).EnsureSuccessStatusCode();
        (await runner.PostAsync($"/api/v1/runner/missions/{git}/acts/open-pull-request", null, Ct)).EnsureSuccessStatusCode();

        // The control plane restarts: what it held of the repair is gone, and a runner's dossier is not a source of it
        factory.Services.GetRequiredService<RepairRegistry>().Forget("p-worlds");
        var afterRestart = await (await runner.PostAsync($"/api/v1/runner/missions/{git}/acts/merge", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        afterRestart.GetProperty("allowed").GetBoolean().Should().BeFalse();
        afterRestart.GetProperty("refusal").GetString().Should().Contain("not in a pull request");
        forge.Merged.Should().BeFalse("the original, red, is not merged in place of the repair");
    }

    [Fact]
    public async Task A_dossier_that_names_a_pull_request_is_not_a_source_of_the_pull_request_of_a_repair()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        await HandOverAsync(runner, (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!, Head, $"artifact:tree={Tree}");
        var git = await NextMissionAsync(factory, runner, "Git");
        (await runner.PostAsync($"/api/v1/runner/missions/{git}/started", null, Ct)).EnsureSuccessStatusCode();
        (await runner.PostAsJsonAsync($"/api/v1/runner/missions/{git}/handover", new { claim = "x", whatChangedAndWhy = "y", evidenceDigests = new[] { $"repair:pr=99;head={new string('a', 40)};from={Head}" }, openPoints = Array.Empty<string>(), complex = false, commit = MergeCommit }, Ct)).EnsureSuccessStatusCode();

        factory.Services.GetRequiredService<RepairRegistry>().FixOf("p-worlds").Should().BeNull();
    }

    [Fact]
    public async Task A_staging_pass_that_repaired_nothing_clears_a_repair_that_an_earlier_pass_held()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        factory.Services.GetRequiredService<RepairRegistry>().Keep("p-worlds", Head, [new PatchedFile("server.js", "old")]);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;

        await HandOverAsync(runner, staging, Head, $"artifact:tree={Tree}");

        factory.Services.GetRequiredService<RepairRegistry>().PendingOf("p-worlds").Should().BeNull("the last pass is the last word");
    }

    [Fact]
    public async Task Only_the_staging_world_hands_over_a_repair()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        await HandOverAsync(runner, (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!, Head, $"artifact:tree={Tree}");
        var git = await NextMissionAsync(factory, runner, "Git");

        var response = await HandOverWithFilesAsync(runner, git, [new { path = "server.js", content = "x" }]);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task One_problem_at_a_time_works_on_a_repository()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        var first = await StartProblemAsync(factory, "p-first");
        var second = await StartProblemAsync(factory, "p-second");
        using var runner = Runner(factory);

        var claimed = await ClaimAsync(runner, "Staging");

        claimed.GetProperty("problemId").GetString().Should().Be(first);
        (await ClaimAsync(runner, "Staging")).GetProperty("problemId").GetString().Should().Be(first, "the second problem waits for the first to leave the repository");
        second.Should().Be("p-second");
    }

    [Fact]
    public async Task The_gateway_does_nothing_for_a_mission_that_has_not_started_or_a_world_that_does_not_list_the_act()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;

        (await runner.PostAsync($"/api/v1/runner/missions/{staging}/acts/merge", null, Ct)).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound, "it has not started");
        (await runner.PostAsync($"/api/v1/runner/missions/{staging}/started", null, Ct)).EnsureSuccessStatusCode();
        var merge = await (await runner.PostAsync($"/api/v1/runner/missions/{staging}/acts/merge", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        merge.GetProperty("allowed").GetBoolean().Should().BeFalse("a staging mandate lists no act");
        forge.Merged.Should().BeFalse();
    }
}
