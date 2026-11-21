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
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly object Hello = new { hello = new { name = "runner-staging", version = "1", kinds = new[] { "validatePullRequest" }, repositories = new[] { "example/*" }, delegations = Array.Empty<object>(), secondFactorRequired = false } };

    /// <summary>A forge in memory: a pull request that is green at its head, merged on demand, whose merge result has a tree and a pipeline of its own.</summary>
    private sealed class FakeForge : IPullRequestFacts, IForgeActs
    {
        public bool Merged { get; private set; }

        public ChecksState HeadChecks { get; set; } = ChecksState.Passing;

        public ChecksState MergeChecks { get; set; } = ChecksState.Passing;

        public string TreeOfTheMerge { get; set; } = Tree;

        public string BaseRef { get; set; } = "main";

        public Task<PullRequestFacts?> ReadAsync(string repositoryFullName, int number, CancellationToken cancellationToken) =>
            Task.FromResult<PullRequestFacts?>(new PullRequestFacts(Head, "base", false, HeadChecks, ["package.json"], PathsComplete: true, Merged, Merged ? MergeCommit : null, Merged ? TreeOfTheMerge : null, Merged ? MergeChecks : ChecksState.Unknown, BaseRef: BaseRef, DefaultBranch: "main"));

        public Task<MergeResult> MergePullRequestAsync(string repositoryFullName, int number, string expectedHeadSha, CancellationToken cancellationToken)
        {
            if (expectedHeadSha != Head)
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
            });
            builder.UseEnvironment("Development");
            builder.UseSetting("Missions:Sandbox", "true");
            builder.UseSetting("Missions:SandboxMaxWorld", "Release");
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
