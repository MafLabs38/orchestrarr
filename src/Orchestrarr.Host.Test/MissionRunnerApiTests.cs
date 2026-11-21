using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

/// <summary>The routes of a runner of missions and the plan of the test bed: a problem is started through the conductor, and a runner with its token takes it, reports and hands the dossier over.</summary>
public sealed class MissionRunnerApiTests
{
    private const string Header = "X-Orchestrarr-Request";
    private static readonly string Token = new('m', 48);
    private static readonly string Head = new('d', 40);
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private sealed class SandboxlessFactory : AccountsApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Tasks:Runners:0:Name", "runner-staging");
            builder.UseSetting("Tasks:Runners:0:Token", Token);
        }
    }

    private sealed class SandboxFactory : AccountsApiFactory
    {
        /// <summary>When set, the controller answers with this text and the forge says that the pull request is green at the head of the mission.</summary>
        public string? ControllerSays { get; init; }

        /// <summary>The runner that the staging mandates name (default: the one whose token the tests hold).</summary>
        public string StagingRunner { get; init; } = "runner-staging";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (ControllerSays is { } says)
            {
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IAgentGateway>(new SaysGateway(says));
                    services.AddSingleton<IPullRequestFacts>(new FakeFacts(new PullRequestFacts(Head, "base", false, ChecksState.Passing, ["package.json"], PathsComplete: true)));
                });
            }
            builder.UseEnvironment("Development");
            builder.UseSetting("Missions:Sandbox", "true");
            builder.UseSetting("Missions:StagingRunner", StagingRunner);
            builder.UseSetting("Tasks:Runners:0:Name", "runner-staging");
            builder.UseSetting("Tasks:Runners:0:Token", Token);
        }
    }

    private static async Task<HttpClient> SignedIn(AccountsApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Header, "test");
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { name = AccountsApiFactory.AdminName, password = AccountsApiFactory.AdminPassword }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    private static HttpClient Runner(AccountsApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static readonly object Hello = new { hello = new { name = "runner-staging", version = "1", kinds = new[] { "validatePullRequest" }, repositories = new[] { "example/*" }, delegations = Array.Empty<object>(), secondFactorRequired = false } };

    private static async Task<string> StartProblemAsync(AccountsApiFactory factory)
    {
        var approvals = factory.Services.GetRequiredService<SandboxApprovals>();
        var conductor = factory.Services.GetRequiredService<ProblemConductor>();
        var entry = new PlanEntry("p-test", "example/shop", "dependency-update", "Prove pull request #7", ["example/shop"], World.Release, new MissionBudget(30, 3, 50), new Validation("alice", null), DateTimeOffset.UtcNow, 7, Head);
        (await approvals.PrepareAsync(entry, Ct)).Should().BeTrue();
        var started = await conductor.StartAsync(entry, StagingDeclaration.DeclaredStaging, StagingRequirement.Dependencies, StagingState.BaselineProven, Ct);
        started.Ok.Should().BeTrue(started.Refusal);
        return started.Mission!.Id;
    }

    [Fact]
    public async Task A_runner_takes_its_mission_says_it_started_reports_and_hands_the_dossier_over()
    {
        using var factory = new SandboxFactory();
        var missionId = await StartProblemAsync(factory);
        using var runner = Runner(factory, Token);

        var claim = await (await runner.PostAsJsonAsync("/api/v1/runner/missions/claim", Hello, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        var mission = claim.GetProperty("mission");
        mission.GetProperty("missionId").GetString().Should().Be(missionId);
        mission.GetProperty("repository").GetString().Should().Be("example/shop");
        mission.GetProperty("pullRequest").GetInt32().Should().Be(7);
        mission.GetProperty("headSha").GetString().Should().Be(Head);
        (await runner.PostAsync($"/api/v1/runner/missions/{missionId}/started", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await runner.PostAsJsonAsync($"/api/v1/runner/missions/{missionId}/progress", new { line = "Building in a container" }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var dossier = new { claim = "It builds and passes", whatChangedAndWhy = "One file", evidenceDigests = new[] { "build:exit=0" }, openPoints = Array.Empty<string>(), complex = false, commit = Head };
        (await runner.PostAsJsonAsync($"/api/v1/runner/missions/{missionId}/handover", dossier, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var person = await SignedIn(factory);
        var view = await person.GetFromJsonAsync<JsonElement>($"/api/v1/missions/{missionId}", Ct);
        view.GetProperty("state").GetString().Should().Be("HandingOver");
        view.GetProperty("dossier").GetProperty("claim").GetString().Should().Be("It builds and passes");
        view.GetProperty("progress").EnumerateArray().Select(line => line.GetString() ?? string.Empty).Should().Contain(line => line.EndsWith("Building in a container", StringComparison.Ordinal));
        (await (await runner.PostAsJsonAsync("/api/v1/runner/missions/claim", Hello, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("mission").ValueKind.Should().Be(JsonValueKind.Null, "a mission that is handed over is not offered again");
    }

    [Fact]
    public async Task When_a_mission_is_handed_over_the_controller_reads_it_and_the_conductor_issues_the_mandate_of_the_next_world()
    {
        using var factory = new SandboxFactory { ControllerSays = """{"decision":"grant","reasons":["the evidence supports the claim"],"remarks":""}""" };
        var missionId = await StartProblemAsync(factory);
        using var runner = Runner(factory, Token);
        (await runner.PostAsync($"/api/v1/runner/missions/{missionId}/started", null, Ct)).EnsureSuccessStatusCode();
        var dossier = new { claim = "It builds and passes", whatChangedAndWhy = "One file", evidenceDigests = new[] { "build:exit=0" }, openPoints = Array.Empty<string>(), complex = false, commit = Head };
        (await runner.PostAsJsonAsync($"/api/v1/runner/missions/{missionId}/handover", dossier, Ct)).EnsureSuccessStatusCode();

        using var person = await SignedIn(factory);
        JsonElement missions = default;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            missions = await person.GetFromJsonAsync<JsonElement>("/api/v1/missions", Ct);
            if (missions.EnumerateArray().Any(mission => mission.GetProperty("world").GetString() == "Git"))
            {
                break;
            }
            await Task.Delay(500, Ct);
        }

        var staging = missions.EnumerateArray().Single(mission => mission.GetProperty("world").GetString() == "Staging");
        staging.GetProperty("state").GetString().Should().Be("Accepted");
        staging.GetProperty("progress").EnumerateArray().Select(line => line.GetString() ?? string.Empty).Should().Contain(line => line.Contains("the controller decided: Grant", StringComparison.Ordinal));
        var git = missions.EnumerateArray().Single(mission => mission.GetProperty("world").GetString() == "Git");
        git.GetProperty("mandate").GetProperty("acts").EnumerateArray().Select(act => act.GetString()).Should().Contain("MergePullRequest");
    }

    [Fact]
    public async Task A_runner_with_the_wrong_token_or_for_a_mission_that_is_not_its_own_gets_nothing()
    {
        using var factory = new SandboxFactory();
        await StartProblemAsync(factory);
        using var stranger = Runner(factory, new string('x', 48));
        using var runner = Runner(factory, Token);

        (await stranger.PostAsJsonAsync("/api/v1/runner/missions/claim", Hello, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await runner.PostAsync("/api/v1/runner/missions/unknown/started", null, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await runner.PostAsJsonAsync("/api/v1/runner/missions/unknown/progress", new { line = "x" }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await runner.PostAsJsonAsync("/api/v1/runner/missions/unknown/handover", new { claim = "x", whatChangedAndWhy = "y" }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_runner_that_the_mandate_does_not_name_is_offered_nothing_and_cannot_touch_the_mission()
    {
        using var factory = new SandboxFactory { StagingRunner = "another-runner" };
        var missionId = await StartProblemAsync(factory);
        using var runner = Runner(factory, Token);

        (await (await runner.PostAsJsonAsync("/api/v1/runner/missions/claim", Hello, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("mission").ValueKind.Should().Be(JsonValueKind.Null);
        (await runner.PostAsync($"/api/v1/runner/missions/{missionId}/started", null, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await runner.PostAsJsonAsync($"/api/v1/runner/missions/{missionId}/progress", new { line = "x" }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await runner.PostAsJsonAsync($"/api/v1/runner/missions/{missionId}/handover", new { claim = "x", whatChangedAndWhy = "y" }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_mission_of_another_world_is_not_offered_and_a_line_is_not_taken_after_the_handover()
    {
        using var factory = new SandboxFactory();
        var missionId = await StartProblemAsync(factory);
        using var runner = Runner(factory, Token);

        (await (await runner.PostAsJsonAsync("/api/v1/runner/missions/claim?world=Git", Hello, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("mission").ValueKind.Should().Be(JsonValueKind.Null, "the mission is of the staging world");
        (await runner.PostAsync($"/api/v1/runner/missions/{missionId}/started", null, Ct)).EnsureSuccessStatusCode();
        var dossier = new { claim = "c", whatChangedAndWhy = "w", evidenceDigests = new[] { "d" }, openPoints = Array.Empty<string>(), complex = false, commit = Head };
        (await runner.PostAsJsonAsync($"/api/v1/runner/missions/{missionId}/handover", dossier, Ct)).EnsureSuccessStatusCode();

        (await runner.PostAsJsonAsync($"/api/v1/runner/missions/{missionId}/progress", new { line = "late" }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_dossier_is_kept_within_bounds_whatever_the_runner_sends()
    {
        using var factory = new SandboxFactory();
        var missionId = await StartProblemAsync(factory);
        using var runner = Runner(factory, Token);
        (await runner.PostAsync($"/api/v1/runner/missions/{missionId}/started", null, Ct)).EnsureSuccessStatusCode();
        var dossier = new { claim = "c", whatChangedAndWhy = "w", evidenceDigests = Enumerable.Repeat(new string('x', 5000), 50).ToArray(), openPoints = Array.Empty<string>(), complex = false, commit = Head };
        (await runner.PostAsJsonAsync($"/api/v1/runner/missions/{missionId}/handover", dossier, Ct)).EnsureSuccessStatusCode();

        using var person = await SignedIn(factory);
        var view = await person.GetFromJsonAsync<JsonElement>($"/api/v1/missions/{missionId}", Ct);

        view.GetProperty("dossier").GetProperty("claim").GetString().Should().Be("c");
        factory.Services.GetRequiredService<IDossierStore>().Get(missionId)!.EvidenceDigests.Should().HaveCount(20).And.OnlyContain(digest => digest.Length == 2000);
    }

    [Fact]
    public async Task A_controller_that_cannot_answer_is_never_a_grant_and_is_not_asked_again_at_once()
    {
        using var factory = new SandboxFactory();
        var missionId = await StartProblemAsync(factory);
        using var runner = Runner(factory, Token);
        (await runner.PostAsync($"/api/v1/runner/missions/{missionId}/started", null, Ct)).EnsureSuccessStatusCode();
        var dossier = new { claim = "c", whatChangedAndWhy = "w", evidenceDigests = new[] { "d" }, openPoints = Array.Empty<string>(), complex = false, commit = Head };
        (await runner.PostAsJsonAsync($"/api/v1/runner/missions/{missionId}/handover", dossier, Ct)).EnsureSuccessStatusCode();
        var driver = factory.Services.GetServices<IHostedService>().OfType<MissionDriver>().Single();

        await driver.PassAsync(Ct);
        var asked = factory.Services.GetRequiredService<MissionProgress>().Lines(missionId).Count(line => line.Contains("reads the dossier", StringComparison.Ordinal));
        await driver.PassAsync(Ct);

        asked.Should().Be(1);
        factory.Services.GetRequiredService<MissionProgress>().Lines(missionId).Count(line => line.Contains("reads the dossier", StringComparison.Ordinal)).Should().Be(1, "it is asked again after a while, not at every pass");
        factory.Services.GetRequiredService<MissionProgress>().Lines(missionId).Should().Contain(line => line.Contains("gave no decision", StringComparison.Ordinal));
        using var person = await SignedIn(factory);
        var missions = await person.GetFromJsonAsync<JsonElement>("/api/v1/missions", Ct);
        missions.GetArrayLength().Should().Be(1, "no mandate of the next world without an answer");
        missions[0].GetProperty("state").GetString().Should().Be("HandingOver");
    }

    [Fact]
    public async Task A_handover_without_a_claim_is_refused()
    {
        using var factory = new SandboxFactory();
        var missionId = await StartProblemAsync(factory);
        using var runner = Runner(factory, Token);
        (await runner.PostAsync($"/api/v1/runner/missions/{missionId}/started", null, Ct)).EnsureSuccessStatusCode();

        (await runner.PostAsJsonAsync($"/api/v1/runner/missions/{missionId}/handover", new { claim = " ", whatChangedAndWhy = "y" }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_claim_makes_the_runner_known_so_that_the_table_offers_what_it_accepts()
    {
        using var factory = new SandboxFactory();
        using var runner = Runner(factory, Token);
        (await runner.PostAsJsonAsync("/api/v1/runner/missions/claim", Hello, Ct)).EnsureSuccessStatusCode();

        using var person = await SignedIn(factory);
        var status = await person.GetFromJsonAsync<JsonElement>("/api/v1/system/status", Ct);
        status.ToString().Should().Contain("runner-staging");
    }

    [Fact]
    public async Task The_plan_is_available_only_where_the_test_bed_approvals_are_wired()
    {
        using var sandbox = new SandboxFactory();
        using var plain = new AccountsApiFactory();
        using var inSandbox = await SignedIn(sandbox);
        using var inPlain = await SignedIn(plain);

        (await inSandbox.GetFromJsonAsync<JsonElement>("/api/v1/plan", Ct)).GetProperty("available").GetBoolean().Should().BeTrue();
        (await inPlain.GetFromJsonAsync<JsonElement>("/api/v1/plan", Ct)).GetProperty("available").GetBoolean().Should().BeFalse();
        (await inPlain.PostAsJsonAsync("/api/v1/plan/validate", new { rows = Array.Empty<object>() }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task A_row_that_is_not_in_the_table_is_not_started()
    {
        using var factory = new SandboxFactory();
        using var person = await SignedIn(factory);

        var response = await person.PostAsJsonAsync("/api/v1/plan/validate", new { rows = new[] { new { id = "pr:example/shop#99", headSha = Head } } }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var problem = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("problems")[0];
        problem.GetProperty("refusal").GetString().Should().Be("theTableChanged");
        problem.GetProperty("missionId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void The_test_bed_sources_are_refused_outside_a_development_host()
    {
        var configuration = new ConfigurationBuilder().Build();

        var attempt = () => new ServiceCollection().AddSandboxMissions(configuration, isDevelopment: false);

        attempt.Should().Throw<InvalidOperationException>().WithMessage("*development host only*");
    }

    [Fact]
    public async Task One_selection_of_several_rows_gets_a_certificate_for_each()
    {
        using var factory = new SandboxFactory();
        var approvals = factory.Services.GetRequiredService<SandboxApprovals>();
        PlanEntry Entry(string id) => new(id, "example/shop", "dependency-update", "x", ["example/shop"], World.Release, new MissionBudget(30, 3, 50), new Validation("alice", null), DateTimeOffset.UtcNow, 7, Head);

        (await approvals.PrepareAsync(Entry("p-a"), Ct)).Should().BeTrue();
        (await approvals.PrepareAsync(Entry("p-b"), Ct)).Should().BeTrue("a code that was used a moment ago is not a reason to refuse the next row of the same selection");
        (await approvals.PrepareAsync(Entry("p-c"), Ct)).Should().BeTrue();

        approvals.For(Entry("p-a")).Should().NotBeNull();
        approvals.For(Entry("p-c")).Should().NotBeNull();
    }

    [Fact]
    public async Task The_plan_needs_a_session_and_the_header_of_a_write()
    {
        using var factory = new SandboxFactory();
        using var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add(Header, "test");
        using var person = await SignedIn(factory);
        using var noHeader = await SignedIn(factory);
        noHeader.DefaultRequestHeaders.Remove(Header);

        (await anonymous.PostAsJsonAsync("/api/v1/plan/validate", new { rows = Array.Empty<object>() }, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await noHeader.PostAsJsonAsync("/api/v1/plan/validate", new { rows = Array.Empty<object>() }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var tooMany = Enumerable.Range(0, 401).Select(n => new { id = $"pr:example/shop#{n}", headSha = Head }).ToArray();
        (await person.PostAsJsonAsync("/api/v1/plan/validate", new { rows = tooMany }, Ct)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task The_routes_of_the_runners_of_missions_are_not_there_without_the_sandbox()
    {
        using var factory = new SandboxlessFactory();
        using var runner = Runner(factory, Token);

        (await runner.PostAsJsonAsync("/api/v1/runner/missions/claim", Hello, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "an unknown route needs a session like every other");
        (await runner.GetAsync("/api/v1/runner/stop", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "the runner of tasks is not affected");
    }

    [Fact]
    public void The_lines_of_a_mission_are_bounded_in_length_in_number_and_in_missions_kept()
    {
        var progress = new MissionProgress(TimeProvider.System);

        progress.Add("m-long", new string('x', 5000));
        for (var line = 0; line < 400; line++)
        {
            progress.Add("m-many", $"line {line}");
        }
        for (var mission = 0; mission < 250; mission++)
        {
            progress.Add($"m-{mission}", "hello");
        }

        progress.Lines("m-long").Should().BeEmpty("the oldest missions are forgotten first");
        progress.Lines("m-249").Should().ContainSingle();
        progress.Lines("m-0").Should().BeEmpty();
        var many = new MissionProgress(TimeProvider.System);
        for (var line = 0; line < 400; line++)
        {
            many.Add("m", $"line {line}");
        }
        many.Lines("m").Should().HaveCount(300).And.ContainSingle(entry => entry.EndsWith("line 399", StringComparison.Ordinal));
        var one = new MissionProgress(TimeProvider.System);
        one.Add("m", new string('x', 5000));
        one.Lines("m").Single().Length.Should().BeLessThan(520);
    }

    [Fact]
    public async Task The_facts_of_a_pull_request_come_from_the_forge_and_the_frame_that_is_declared_holds()
    {
        var problems = new InMemoryProblemStore();
        var entry = new PlanEntry("p-1", "example/shop", "dependency-update", "x", ["example/shop"], World.Release, new MissionBudget(30, 3, 50), new Validation("alice", null), DateTimeOffset.UtcNow, 7, Head);
        problems.Save(new ProblemRun("p-1", "example/shop", ProblemState.InProgress, DateTimeOffset.UtcNow, entry, World.Git, true, null!));
        var forge = new FakeFacts(new PullRequestFacts(Head, "base", false, ChecksState.Passing, ["package.json", ".github/workflows/ci.yml"], PathsComplete: true));
        var facts = new SandboxFacts(problems, forge);
        var dossier = new Dossier("m1", "claim", "why", [], [], false, null, new string('e', 40));

        var seen = await facts.FactsAsync(new FactsQuery("p-1", "m1", World.Staging, new MissionFrame(["example/shop"], [], "place"), dossier, null), Ct);

        seen.CiGreenOnExactCommit.Should().BeTrue();
        seen.ExecutedFilesTouched.Should().BeTrue("a pipeline file is among the changed paths");
        seen.WorkerClaimMatchesFacts.Should().BeFalse("the dossier names another commit than the head of the pull request");
        seen.HeadCommit.Should().Be(Head);
        seen.MergeMayDeploy.Should().BeFalse();

        var unclaimed = await facts.FactsAsync(new FactsQuery("p-1", "m1", World.Staging, new MissionFrame(["example/shop"], [], "place"), dossier with { Commit = null }, null), Ct);
        unclaimed.WorkerClaimMatchesFacts.Should().BeFalse("a dossier that names no commit does not match the head, it fails the check");

        var unknown = await facts.FactsAsync(new FactsQuery("nobody", null, World.Staging, new MissionFrame([], [], "place"), null, null), Ct);
        unknown.BudgetAvailable.Should().BeFalse();
    }

    private sealed class SaysGateway(string text) : IAgentGateway
    {
        public Task<AgentReply> RunAsync(AgentRequest request, CancellationToken cancellationToken) => Task.FromResult(new AgentReply(true, text, null));
    }

    private sealed class FakeFacts(PullRequestFacts? facts) : IPullRequestFacts
    {
        public Task<PullRequestFacts?> ReadAsync(string repositoryFullName, int number, CancellationToken cancellationToken) => Task.FromResult(facts);
    }
}
