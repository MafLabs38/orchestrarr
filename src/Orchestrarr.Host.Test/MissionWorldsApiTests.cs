using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Launch;
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
    private sealed class FakeForge : IPullRequestFacts, IForgeActs, IDefaultBranchSource, IBranchSource, IPushCredentials
    {
        /// <summary>The push credentials that the control plane asked the forge to make: for which repository, and whether with the permission on the pipelines.</summary>
        public List<(string Repository, bool Pipelines)> CredentialsMade { get; } = [];

        public const string PushToken = "push-token-of-the-forge";

        /// <summary>The credentials revoked on the forge, in order.</summary>
        public List<string> Revoked { get; } = [];

        public Task<PushCredential?> IssueAsync(string repositoryFullName, bool pipelines, CancellationToken cancellationToken)
        {
            CredentialsMade.Add((repositoryFullName, pipelines));
            return Task.FromResult<PushCredential?>(new PushCredential($"{PushToken}-{CredentialsMade.Count}", DateTimeOffset.UtcNow.AddHours(1), pipelines ? ["contents:write", "workflows:write"] : ["contents:write"]));
        }

        /// <summary>Whether the forge fails to revoke (down, a time-out): every try is still recorded.</summary>
        public bool RevokeFails { get; set; }

        public Task<bool> RevokeAsync(string token, CancellationToken cancellationToken)
        {
            lock (Revoked)
            {
                Revoked.Add(token);
            }
            return Task.FromResult(!RevokeFails);
        }

        /// <summary>Where the branch of the agent is on the forge now (null: there is none).</summary>
        public string? BranchHead { get; set; } = AgentSha;

        /// <summary>What the forge says the branch holds on top of the default branch.</summary>
        public BranchChange? Change { get; set; } = new(Head, 0, 1, [new BranchFile("package.json", "modified", 2, "@@ -1 +1 @@\n-a\n+b")], true);

        /// <summary>Who made the pull requests, and what the forge says of their merge (behind, in conflict...).</summary>
        public string? Author { get; set; }

        /// <summary>Who wrote each commit of the pull request; null: every commit is its author's.</summary>
        public IReadOnlyList<string>? CommitAuthors { get; set; }

        /// <summary>The account the forge recorded as having pushed the head (null: the author of the pull request).</summary>
        public string? Pusher { get; set; }

        /// <summary>The commands that the bots were asked to rebase with, by pull request.</summary>
        public List<(int Number, string Command)> RebaseCommands { get; } = [];

        public MergeState PullMerge { get; set; } = MergeState.Unknown;

        /// <summary>The pull requests that the starter asked a bot to rebase.</summary>
        public List<int> RebaseAsked { get; } = [];

        public Task<bool> RequestBotRebaseAsync(string repositoryFullName, int number, string command, CancellationToken cancellationToken)
        {
            RebaseAsked.Add(number);
            RebaseCommands.Add((number, command));
            return Task.FromResult(true);
        }

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
            Task.FromResult<PullRequestFacts?>(new PullRequestFacts(Opened.Count > 0 && number == RepairNumber ? PullHead : Head, "base", false, Opened.Count > 0 && number == RepairNumber ? ChecksState.Passing : HeadChecks, ["package.json"], PathsComplete: true, Merged, Merged ? MergeCommit : null, Merged ? TreeOfTheMerge : null, Merged ? MergeChecks : ChecksState.Unknown, BaseRef: BaseRef, DefaultBranch: "main", Author: Author, Merge: PullMerge, Closed: Closed.Contains(number), CommitAuthors: CommitAuthors ?? (Author is null ? null : [Author]), HeadPushedBy: Pusher ?? Author));

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
                services.AddSingleton<IPushCredentials>(forge);
                // A trail that a test can make refuse every entry, as a full disk would
                services.AddSingleton<SwitchableAudit>();
                services.AddSingleton<IMissionAudit>(provider => provider.GetRequiredService<SwitchableAudit>().Over(new AuditTrailMissions(provider.GetRequiredService<IAuditSink>(), provider.GetRequiredService<TimeProvider>())));
            });
            builder.UseEnvironment("Development");
            builder.UseSetting("Missions:Sandbox", "true");
            builder.UseSetting("Missions:SandboxMaxWorld", "Release");
            builder.UseSetting("Missions:ForbiddenPaths:0", ".github/**");
            builder.UseSetting("Missions:ForbiddenPaths:1", "infra/prod/**");
            builder.UseSetting("Missions:BotRebaseRecheckSeconds", "0");
            builder.UseSetting("Missions:BotRebaseUnknownWaitSeconds", "0");
            builder.UseSetting("Missions:StagingRunner", "runner-staging");
            builder.UseSetting("Missions:GitRunner", "runner-staging");
            builder.UseSetting("Missions:ReleaseRunner", "runner-staging");
            builder.UseSetting("Tasks:Runners:0:Name", "runner-staging");
            builder.UseSetting("Tasks:Runners:0:Token", Token);
        }
    }

    /// <summary>The trail of the missions, which a test can switch off: every entry is then refused.</summary>
    private sealed class SwitchableAudit : IMissionAudit
    {
        private IMissionAudit? _inner;

        public bool Writable { get; set; } = true;

        public SwitchableAudit Over(IMissionAudit inner)
        {
            _inner = inner;
            return this;
        }

        public ValueTask<bool> RecordAsync(string eventName, string subject, string? detail, CancellationToken cancellationToken) =>
            Writable && _inner is not null ? _inner.RecordAsync(eventName, subject, detail, cancellationToken) : ValueTask.FromResult(false);
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
        (await runner.PostAsJsonAsync($"/api/v1/runner/missions/{release}/handover", new { claim = "deployed and verified", whatChangedAndWhy = "container", evidenceDigests = new[] { "deploy:exit=0:sha256=aa", "verify:exit=0:sha256=bb" }, openPoints = Array.Empty<string>(), complex = false, commit = MergeCommit }, Ct)).EnsureSuccessStatusCode();
        var driver = factory.Services.GetServices<IHostedService>().OfType<MissionDriver>().Single();
        await driver.PassAsync(Ct);

        factory.Services.GetRequiredService<IProblemStore>().Get("p-worlds")!.State.Should().Be(ProblemState.Closed);
        factory.Services.GetRequiredService<IMissionStore>().ForProblem("p-worlds").Select(mission => mission.World).Should().Equal(World.Staging, World.Git, World.Release);
    }

    private static Task<string?> ClaimAsyncById(AccountsApiFactory factory, string missionId) =>
        Task.FromResult(factory.Services.GetRequiredService<ProblemConductor>().AcceptedCommit(missionId));

    [Theory]
    [InlineData(MissionEvidence.RefusedConflict, "conflicts with its base branch")]
    [InlineData(MissionEvidence.RefusedMoved, "moved since the maintainer approved it")]
    public async Task A_refusal_that_no_agent_mends_ends_the_problem_at_once_without_the_controller(string marker, string said)
    {
        // No answer is scripted for the controller: it would be asked and fail, so a pass that ends the problem proves that it was not asked
        using var factory = new WorldsFactory(new FakeForge());
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;
        await HandOverAsync(runner, staging, null, marker);

        await factory.Services.GetServices<IHostedService>().OfType<MissionDriver>().Single().PassAsync(Ct);

        factory.Services.GetRequiredService<IProblemStore>().Get("p-worlds")!.State.Should().NotBe(ProblemState.InProgress);
        factory.Services.GetRequiredService<IMissionStore>().ForProblem("p-worlds").Should().ContainSingle().Which.State.Should().Be(MissionState.Ended);
        factory.Services.GetRequiredService<MissionProgress>().Lines(staging).Should().Contain(line => line.Contains(said, StringComparison.Ordinal));
    }

    private static PreparedProblem Prepared(string problemId, int number, params string[] repositories) =>
        new($"row-{problemId}", new PlanEntry(problemId, "example/shop", "dependency-update", $"Prove pull request #{number}", repositories, World.Release, new MissionBudget(30, 3, 50), new Validation("alice", null), DateTimeOffset.UtcNow, number, Head), null);

    [Fact]
    public async Task A_problem_of_a_free_repository_starts_and_the_next_one_on_it_waits_without_starting()
    {
        using var factory = new WorldsFactory(new FakeForge());
        var starter = factory.Services.GetRequiredService<ProblemStarter>();

        var first = await starter.StartOrQueueAsync(Prepared("p-a", 7, "example/shop"), Ct);
        var second = await starter.StartOrQueueAsync(Prepared("p-b", 8, "example/shop"), Ct);
        var elsewhere = await starter.StartOrQueueAsync(Prepared("p-c", 9, "example/other"), Ct);

        first.MissionId.Should().NotBeNull(first.Refusal);
        second.Should().BeEquivalentTo(new ValidatedProblem("row-p-b", "p-b", null, null, null, Queued: true));
        elsewhere.MissionId.Should().NotBeNull("another repository is free: it starts at once");
        factory.Services.GetRequiredService<IProblemStore>().Get("p-b").Should().BeNull("a problem that waits is planned, not started");
        // the same problem validated again while it waits is not put in line twice
        (await starter.StartOrQueueAsync(Prepared("p-b", 8, "example/shop"), Ct)).Refusal.Should().Be("alreadyQueued");
    }

    private static async Task<HttpClient> Operator(AccountsApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Orchestrarr-Request", "test");
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { name = AccountsApiFactory.AdminName, password = AccountsApiFactory.AdminPassword }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task A_held_operation_does_not_start_never_holds_its_lot_and_starts_when_a_person_releases_it()
    {
        using var factory = new WorldsFactory(new FakeForge());
        var starter = factory.Services.GetRequiredService<ProblemStarter>();
        var problems = factory.Services.GetRequiredService<IProblemStore>();
        using var person = await Operator(factory);
        _ = await starter.StartOrQueueAsync(Prepared("p-first", 7, "example/shop"), Ct);
        _ = await starter.StartOrQueueAsync(Prepared("p-held", 8, "example/shop"), Ct);
        _ = await starter.StartOrQueueAsync(NoPullRequest("p-next", "example/shop"), Ct);

        var before = (await person.GetFromJsonAsync<JsonElement>("/api/v1/flow/queue", Ct)).GetProperty("line").EnumerateArray().ToList();
        before.Select(entry => (entry.GetProperty("problemId").GetString(), entry.GetProperty("next").GetBoolean())).Should().Equal(("p-held", true), ("p-next", false));
        (await person.PostAsJsonAsync("/api/v1/flow/p-held/hold", new { reason = "" }, Ct)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "a hold says why");
        (await person.PostAsJsonAsync("/api/v1/flow/p-held/hold", new { reason = "   \u202e" }, Ct)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "spaces and format characters are no reason");
        (await person.PostAsJsonAsync("/api/v1/flow/p-held/hold", new { reason = new string('x', ProblemHolds.MaxReason + 1) }, Ct)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "a reason is bounded");
        (await person.PostAsJsonAsync("/api/v1/flow/p-unknown/hold", new { reason = "look first" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict, "only an operation of the line is held");
        (await person.PostAsJsonAsync("/api/v1/flow/p-held/hold", new { reason = "an analysis first" }, Ct)).EnsureSuccessStatusCode();
        var line = await person.GetFromJsonAsync<JsonElement>("/api/v1/flow/queue", Ct);
        var held = line.GetProperty("line").EnumerateArray().Single(entry => entry.GetProperty("problemId").GetString() == "p-held");
        held.GetProperty("state").GetString().Should().Be("held");
        held.GetProperty("held").GetProperty("reason").GetString().Should().Be("an analysis first");
        held.GetProperty("held").GetProperty("by").GetString().Should().Be(AccountsApiFactory.AdminName, "a person reads who held it by name");
        held.GetProperty("next").GetBoolean().Should().BeFalse();
        line.GetProperty("line").EnumerateArray().Single(entry => entry.GetProperty("problemId").GetString() == "p-next").GetProperty("next").GetBoolean().Should().BeTrue("a held operation never holds its lot");

        // the first one ends: the lot is free, and the held one lets the next of the route go
        problems.Save(problems.Get("p-first")! with { State = ProblemState.Closed });
        await starter.StartReadyAsync(Ct);

        problems.Get("p-next")!.State.Should().Be(ProblemState.InProgress, "a held operation never holds its lot");
        problems.Get("p-held").Should().BeNull();
        problems.Save(problems.Get("p-next")! with { State = ProblemState.Closed });
        await starter.StartReadyAsync(Ct);
        problems.Get("p-held").Should().BeNull("it waits for a person to release it");

        (await person.PostAsync("/api/v1/flow/p-held/release", null, Ct)).EnsureSuccessStatusCode();
        await starter.StartReadyAsync(Ct);
        problems.Get("p-held")!.State.Should().Be(ProblemState.InProgress);
        (await person.PostAsync("/api/v1/flow/p-held/release", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict, "what is not held is not released");
        var traveler = await person.GetFromJsonAsync<JsonElement>("/api/v1/problems/p-held/traveler", Ct);
        traveler.GetProperty("steps").EnumerateArray().Select(step => step.GetProperty("event").GetString()).Should().ContainInOrder("problem.queued", "problem.held", "problem.released", "problem.started");
        (await person.GetAsync("/api/v1/problems/p-unknown/traveler", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_priority_set_by_a_person_orders_the_line_and_goes_with_the_operation_when_it_starts()
    {
        using var factory = new WorldsFactory(new FakeForge());
        var starter = factory.Services.GetRequiredService<ProblemStarter>();
        var problems = factory.Services.GetRequiredService<IProblemStore>();
        using var person = await Operator(factory);
        _ = await starter.StartOrQueueAsync(Prepared("p-first", 7, "example/shop"), Ct);
        _ = await starter.StartOrQueueAsync(Prepared("p-early", 8, "example/shop"), Ct);
        _ = await starter.StartOrQueueAsync(Prepared("p-late", 9, "example/shop"), Ct);

        (await person.PostAsJsonAsync("/api/v1/flow/p-late/priority", new { priority = 101 }, Ct)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await person.PostAsJsonAsync("/api/v1/flow/p-late/priority", new { priority = 80 }, Ct)).EnsureSuccessStatusCode();
        (await person.PostAsJsonAsync("/api/v1/flow/p-first/priority", new { priority = 60 }, Ct)).EnsureSuccessStatusCode();

        var line = await person.GetFromJsonAsync<JsonElement>("/api/v1/flow/queue", Ct);
        line.GetProperty("line").EnumerateArray().Select(entry => entry.GetProperty("problemId").GetString()).Should().Equal("p-late", "p-early");
        line.GetProperty("line")[0].GetProperty("next").GetBoolean().Should().BeTrue();
        line.GetProperty("inProcess").EnumerateArray().Single().GetProperty("priority").GetInt32().Should().Be(60, "an operation in process takes its priority too");
        problems.Save(problems.Get("p-first")! with { State = ProblemState.Closed });
        await starter.StartReadyAsync(Ct);

        problems.Get("p-late")!.Priority.Should().Be(80);
        problems.Get("p-early").Should().BeNull();
        (await person.PostAsJsonAsync("/api/v1/flow/p-nothing/priority", new { priority = 60 }, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_bots_pull_request_that_is_behind_its_base_is_parked_and_the_lot_goes_on_with_its_next_operation()
    {
        var forge = new FakeForge { Author = "dependabot[bot]", PullMerge = MergeState.Conflicting };
        using var factory = new WorldsFactory(forge);
        var starter = factory.Services.GetRequiredService<ProblemStarter>();
        var line = factory.Services.GetRequiredService<ProblemQueue>();

        var parked = await starter.StartOrQueueAsync(Prepared("p-bot", 7, "example/shop"), Ct);
        // the next operation of the lot has no pull request (an alert, an image): nothing to judge, the lot is free, so it starts
        var next = await starter.StartOrQueueAsync(NoPullRequest("p-next", "example/shop"), Ct);

        parked.Queued.Should().BeTrue("the bot is asked and the operation waits");
        forge.RebaseCommands.Should().Equal([(7, "@dependabot rebase")], "each bot is asked with its own command");
        factory.Services.GetRequiredService<ProblemParking>().IsParked("p-bot").Should().BeTrue();
        line.Contains("p-bot").Should().BeTrue();
        next.MissionId.Should().NotBeNull("a parked operation does not hold its lot");
        factory.Services.GetRequiredService<IProblemStore>().Get("p-bot").Should().BeNull("a parked operation is planned, not started");
    }

    [Fact]
    public async Task A_bot_that_has_no_command_to_rebase_is_not_waited_for_and_its_pull_request_is_put_aside_at_once()
    {
        var forge = new FakeForge { Author = "renovate[bot]", PullMerge = MergeState.Conflicting };
        using var factory = new WorldsFactory(forge);
        var starter = factory.Services.GetRequiredService<ProblemStarter>();

        var answer = await starter.StartOrQueueAsync(Prepared("p-bot", 7, "example/shop"), Ct);

        answer.Refusal.Should().Be("putAside", "the answer says what the flow did, not that it waits");
        forge.RebaseAsked.Should().BeEmpty("nothing is written on a pull request in a language its bot does not speak");
        factory.Services.GetRequiredService<ProblemParking>().IsParked("p-bot").Should().BeFalse();
        var run = factory.Services.GetRequiredService<IProblemStore>().Get("p-bot")!;
        run.State.Should().Be(ProblemState.Failed);
        run.Note.Should().Contain("renovate[bot]").And.Contain("no command");
    }

    [Fact]
    public async Task A_bot_is_asked_to_rebase_a_bounded_number_of_times_over_the_whole_life_of_the_problem()
    {
        var forge = new FakeForge { Author = "dependabot[bot]", PullMerge = MergeState.Conflicting };
        using var factory = new WorldsFactory(forge);
        var starter = factory.Services.GetRequiredService<ProblemStarter>();
        _ = await starter.StartOrQueueAsync(Prepared("p-bot", 7, "example/shop"), Ct);
        var parking = factory.Services.GetRequiredService<ProblemParking>();
        // the lot was held meanwhile, so this wait starts again; but the bot was already asked four times in all (Missions:BotRebaseRequestsInAll)
        parking.Set("p-bot", new ParkedProblem(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(31), 2, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(31), DateTimeOffset.UtcNow - TimeSpan.FromMinutes(31), "it conflicts with its base branch", AllRequests: 4));
        parking.LotBusy("p-bot");
        forge.RebaseAsked.Clear();

        await starter.StartReadyAsync(Ct);

        forge.RebaseAsked.Should().BeEmpty("a bot is not asked again and again over the life of a problem");
        parking.IsParked("p-bot").Should().BeTrue("it still waits, until the wait is over");
    }

    [Fact]
    public async Task A_parked_pull_request_that_its_bot_brought_up_to_date_starts_when_its_lot_is_free()
    {
        var forge = new FakeForge { Author = "dependabot[bot]", PullMerge = MergeState.Behind };
        using var factory = new WorldsFactory(forge);
        var starter = factory.Services.GetRequiredService<ProblemStarter>();
        var line = factory.Services.GetRequiredService<ProblemQueue>();
        _ = await starter.StartOrQueueAsync(Prepared("p-bot", 7, "example/shop"), Ct);
        forge.PullMerge = MergeState.Clean;

        await starter.StartReadyAsync(Ct);

        // the forge still says the head it always said, so the pull request is ready as ticked: it starts, and is not parked any more
        factory.Services.GetRequiredService<ProblemParking>().IsParked("p-bot").Should().BeFalse();
        line.Contains("p-bot").Should().BeFalse();
        factory.Services.GetRequiredService<IProblemStore>().Get("p-bot").Should().NotBeNull("it started");
        factory.Services.GetRequiredService<IMissionStore>().ForProblem("p-bot").Should().NotBeEmpty("its first mission was issued");
    }

    [Fact]
    public async Task A_bots_pull_request_that_the_runner_refused_goes_back_in_the_line_on_its_new_head_a_bounded_number_of_times()
    {
        var forge = new FakeForge { Author = "dependabot[bot]" };
        using var factory = new WorldsFactory(forge);
        var starter = factory.Services.GetRequiredService<ProblemStarter>();
        var line = factory.Services.GetRequiredService<ProblemQueue>();
        _ = await starter.StartOrQueueAsync(Prepared("p-bot", 7, "example/shop"), Ct);

        await starter.RequeueAfterRefusalAsync("p-bot", Ct);
        line.Contains("p-bot").Should().BeTrue();
        line.Remove("p-bot");
        await starter.RequeueAfterRefusalAsync("p-bot", Ct);
        line.Remove("p-bot");
        await starter.RequeueAfterRefusalAsync("p-bot", Ct);

        line.Contains("p-bot").Should().BeFalse("two goes back in the line at most (Missions:BotRebaseRequests)");
    }

    [Fact]
    public async Task A_pull_request_that_is_not_a_bots_is_never_put_back_in_the_line_after_a_refusal()
    {
        var forge = new FakeForge { Author = "alice" };
        using var factory = new WorldsFactory(forge);
        var starter = factory.Services.GetRequiredService<ProblemStarter>();
        _ = await starter.StartOrQueueAsync(Prepared("p-human", 7, "example/shop"), Ct);

        await starter.RequeueAfterRefusalAsync("p-human", Ct);

        factory.Services.GetRequiredService<ProblemQueue>().Contains("p-human").Should().BeFalse();
    }

    [Fact]
    public async Task A_parked_pull_request_that_its_bot_never_rebases_is_put_aside_with_its_reason_where_the_work_is_followed()
    {
        var forge = new FakeForge { Author = "dependabot[bot]", PullMerge = MergeState.Conflicting };
        using var factory = new WorldsFactory(forge);
        var starter = factory.Services.GetRequiredService<ProblemStarter>();
        _ = await starter.StartOrQueueAsync(Prepared("p-bot", 7, "example/shop"), Ct);
        // it has been parked for longer than the bot is given
        factory.Services.GetRequiredService<ProblemParking>().Set("p-bot", new ParkedProblem(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(31), 2, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow - TimeSpan.FromMinutes(16), "it conflicts with its base branch"));

        await starter.StartReadyAsync(Ct);

        factory.Services.GetRequiredService<ProblemQueue>().Contains("p-bot").Should().BeFalse();
        var run = factory.Services.GetRequiredService<IProblemStore>().Get("p-bot")!;
        run.State.Should().Be(ProblemState.Failed);
        run.Note.Should().Contain("the bot did not rebase").And.Contain("put aside before it started");
    }

    [Fact]
    public async Task The_clock_of_a_parked_pull_request_does_not_run_while_another_operation_of_its_lot_holds_it()
    {
        var forge = new FakeForge { Author = "dependabot[bot]", PullMerge = MergeState.Conflicting };
        using var factory = new WorldsFactory(forge);
        var starter = factory.Services.GetRequiredService<ProblemStarter>();
        _ = await starter.StartOrQueueAsync(Prepared("p-bot", 7, "example/shop"), Ct);
        // parked 31 minutes ago, past its wait: but another operation of its lot held the lot meanwhile, so its bot's clock did not run
        var parking = factory.Services.GetRequiredService<ProblemParking>();
        parking.Set("p-bot", new ParkedProblem(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(31), 2, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(31), DateTimeOffset.UtcNow - TimeSpan.FromMinutes(31), "it conflicts with its base branch", AllRequests: 2));
        parking.LotBusy("p-bot");

        await starter.StartReadyAsync(Ct);

        factory.Services.GetRequiredService<ProblemQueue>().Contains("p-bot").Should().BeTrue("the wait starts again when the lot is free: the base moved meanwhile");
        var parked = factory.Services.GetRequiredService<ProblemParking>().Get("p-bot")!;
        parked.Since.Should().BeAfter(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
        parked.Requests.Should().Be(1, "the bot is asked again at once, on the base as it is now");
        parked.AllRequests.Should().Be(3, "the requests are counted over the whole life of the problem");
    }

    [Fact]
    public async Task A_waiting_pull_request_that_its_bot_closed_because_another_change_brought_it_is_done_not_failed()
    {
        var forge = new FakeForge { Author = "dependabot[bot]", PullMerge = MergeState.Conflicting };
        using var factory = new WorldsFactory(forge);
        var starter = factory.Services.GetRequiredService<ProblemStarter>();
        _ = await starter.StartOrQueueAsync(Prepared("p-bot", 7, "example/shop"), Ct);
        forge.Closed.Add(7);

        await starter.StartReadyAsync(Ct);

        var run = factory.Services.GetRequiredService<IProblemStore>().Get("p-bot")!;
        run.State.Should().Be(ProblemState.Closed);
        run.Note.Should().Contain("done by another operation");
    }

    [Fact]
    public async Task A_pull_request_that_moved_and_is_not_a_bots_is_not_started_and_the_maintainer_ticks_it_again()
    {
        var forge = new FakeForge { Author = "alice" };
        using var factory = new WorldsFactory(forge);
        var starter = factory.Services.GetRequiredService<ProblemStarter>();
        var moved = Prepared("p-moved", 7, "example/shop");

        var answer = await starter.StartOrQueueAsync(moved with { Entry = moved.Entry! with { HeadSha = "0000000000000000000000000000000000000001" } }, Ct);

        answer.Refusal.Should().Be("thePullRequestMoved");
        factory.Services.GetRequiredService<IProblemStore>().Get("p-moved").Should().BeNull();
    }

    [Fact]
    public async Task A_pull_request_that_its_bot_made_again_is_followed_on_the_new_head()
    {
        var forge = new FakeForge { Author = "dependabot[bot]" };
        using var factory = new WorldsFactory(forge);
        var starter = factory.Services.GetRequiredService<ProblemStarter>();
        var again = Prepared("p-again", 7, "example/shop");

        var answer = await starter.StartOrQueueAsync(again with { Entry = again.Entry! with { HeadSha = "0000000000000000000000000000000000000001" } }, Ct);

        answer.MissionId.Should().NotBeNull(answer.Refusal);
        factory.Services.GetRequiredService<IProblemStore>().Get("p-again")!.Entry.HeadSha.Should().Be(Head, "the work follows the head that the bot made");
        factory.Services.GetRequiredService<Orchestrarr.Core.Approvals.IAuditLog>().Page(100).Select(row => row.Entry.Event).Should().Contain(MissionAuditEvents.ProblemFollowedBot, "following a new head is in the trail");
    }

    [Fact]
    public async Task A_new_head_that_another_account_pushed_is_not_followed_even_when_its_commits_claim_to_be_the_bots()
    {
        var forge = new FakeForge { Author = "dependabot[bot]", Pusher = "orchestrarr-sandbox-agent[bot]" };
        using var factory = new WorldsFactory(forge);
        var again = Prepared("p-again", 7, "example/shop");

        var answer = await factory.Services.GetRequiredService<ProblemStarter>().StartOrQueueAsync(again with { Entry = again.Entry! with { HeadSha = "0000000000000000000000000000000000000001" } }, Ct);

        answer.MissionId.Should().BeNull("the author and the signature of a commit can be made to look like the bot's; the account that pushed, as the forge recorded it, cannot");
        answer.Refusal.Should().Be("thePullRequestMoved");
    }

    [Fact]
    public async Task A_bots_branch_that_someone_else_pushed_to_is_not_followed_and_is_ticked_again()
    {
        var forge = new FakeForge { Author = "dependabot[bot]", CommitAuthors = ["dependabot[bot]", "orchestrarr-agent"] };
        using var factory = new WorldsFactory(forge);
        var starter = factory.Services.GetRequiredService<ProblemStarter>();
        var again = Prepared("p-pushed", 7, "example/shop");

        var answer = await starter.StartOrQueueAsync(again with { Entry = again.Entry! with { HeadSha = "0000000000000000000000000000000000000001" } }, Ct);

        answer.Refusal.Should().Be("thePullRequestMoved", "the author of the pull request stays the bot whoever pushes to its branch: a head with someone else's commit is not the bot's");
        factory.Services.GetRequiredService<IProblemStore>().Get("p-pushed").Should().BeNull();
    }

    private static PreparedProblem NoPullRequest(string problemId, params string[] repositories) =>
        new($"row-{problemId}", new PlanEntry(problemId, "example/shop", "DependencyAlert", $"Solve {problemId}", repositories, World.Release, new MissionBudget(30, 3, 50), new Validation("alice", null), DateTimeOffset.UtcNow, null, null), null);

    [Fact]
    public async Task Two_problems_validated_at_the_same_time_on_one_repository_never_both_start()
    {
        using var factory = new WorldsFactory(new FakeForge());
        var starter = factory.Services.GetRequiredService<ProblemStarter>();

        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => starter.StartOrQueueAsync(Prepared($"p-{index}", 10 + index, "example/shop"), Ct)));

        answers.Count(answer => answer.MissionId is not null).Should().Be(1);
        answers.Count(answer => answer.Queued).Should().Be(5);
        factory.Services.GetRequiredService<IProblemStore>().InProgress().Should().ContainSingle();
    }

    [Fact]
    public async Task A_problem_that_is_not_on_exactly_one_repository_does_not_wait_and_is_the_conductors_to_refuse()
    {
        using var factory = new WorldsFactory(new FakeForge());
        var starter = factory.Services.GetRequiredService<ProblemStarter>();

        var answer = await starter.StartOrQueueAsync(Prepared("p-many", 7, "example/shop", "example/other"), Ct);

        answer.Queued.Should().BeFalse();
        answer.Refusal.Should().NotBeNull();
        factory.Services.GetRequiredService<ProblemQueue>().Waiting().Should().BeEmpty();
    }

    [Fact]
    public async Task The_global_stop_empties_the_line_and_nothing_starts()
    {
        using var factory = new WorldsFactory(new FakeForge());
        var starter = factory.Services.GetRequiredService<ProblemStarter>();
        _ = await starter.StartOrQueueAsync(Prepared("p-a", 7, "example/shop"), Ct);
        _ = await starter.StartOrQueueAsync(Prepared("p-b", 8, "example/shop"), Ct);
        _ = await factory.Services.GetRequiredService<IKillSwitchControl>().ChangeAsync(true, new AuditEntry(DateTimeOffset.UtcNow, AuditEvents.KillSwitchEngaged, "alice", null, null), Ct);

        await factory.Services.GetServices<IHostedService>().OfType<ProblemQueueDriver>().Single().PassAsync(Ct);

        factory.Services.GetRequiredService<ProblemQueue>().Waiting().Should().BeEmpty();
        factory.Services.GetRequiredService<IProblemStore>().Get("p-b").Should().BeNull("nothing starts on a validation that a stop interrupted");
    }

    [Fact]
    public async Task A_problem_that_waited_longer_than_its_approval_lasts_leaves_the_line_without_starting()
    {
        using var factory = new WorldsFactory(new FakeForge());
        var line = factory.Services.GetRequiredService<ProblemQueue>();
        _ = await factory.Services.GetRequiredService<ProblemStarter>().StartOrQueueAsync(Prepared("p-a", 7, "example/shop"), Ct);
        var entry = Prepared("p-old", 8, "example/shop").Entry!;
        line.Enqueue(new QueuedProblem("row-old", entry, DateTimeOffset.UtcNow - ProblemStarter.QueuedLifetime - TimeSpan.FromMinutes(1))).Should().BeTrue();

        await factory.Services.GetRequiredService<ProblemStarter>().StartReadyAsync(Ct);

        line.Contains("p-old").Should().BeFalse();
        factory.Services.GetRequiredService<IProblemStore>().Get("p-old").Should().BeNull();
    }

    [Fact]
    public async Task A_refusal_marker_outside_the_staging_world_is_only_a_claim_and_ends_nothing()
    {
        // The staging is granted, then the Git world claims a refusal: the marker ends nothing there, the dossier goes to the controller, which here has no answer left, so the mission waits where it is
        using var factory = new WorldsFactory(new FakeForge(), "grant");
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;
        await HandOverAsync(runner, staging, Head, $"artifact:tree={Tree}");
        var driver = factory.Services.GetServices<IHostedService>().OfType<MissionDriver>().Single();
        var git = await NextMissionAsync(factory, runner, "Git");
        await HandOverAsync(runner, git, MergeCommit, MissionEvidence.RefusedConflict);

        await driver.PassAsync(Ct);

        factory.Services.GetRequiredService<MissionProgress>().Lines(git).Should().NotContain(line => line.Contains("the runner refused before building", StringComparison.Ordinal));
        factory.Services.GetRequiredService<IProblemStore>().Get("p-worlds")!.State.Should().Be(ProblemState.InProgress);
    }

    [Fact]
    public async Task A_problem_waits_for_its_repository_and_starts_when_the_one_before_it_has_ended()
    {
        using var factory = new WorldsFactory(new FakeForge());
        _ = await StartProblemAsync(factory, "p-first");
        var line = factory.Services.GetRequiredService<ProblemQueue>();
        var second = new PlanEntry("p-second", "example/shop", "dependency-update", "Prove pull request #8", ["example/shop"], World.Release, new MissionBudget(30, 3, 50), new Validation("alice", null), DateTimeOffset.UtcNow, 8, Head);
        line.Enqueue(new QueuedProblem("row-second", second, DateTimeOffset.UtcNow)).Should().BeTrue();
        var driver = factory.Services.GetServices<IHostedService>().OfType<ProblemQueueDriver>().Single();
        var problems = factory.Services.GetRequiredService<IProblemStore>();

        await driver.PassAsync(Ct);
        problems.Get("p-second").Should().BeNull("the repository is busy: nothing starts on it while another problem is in progress");
        line.Contains("p-second").Should().BeTrue();

        (await factory.Services.GetRequiredService<ProblemConductor>().StopAsync("p-first", "test", Ct)).Ok.Should().BeTrue();
        await driver.PassAsync(Ct);

        problems.Get("p-second")!.State.Should().Be(ProblemState.InProgress);
        line.Contains("p-second").Should().BeFalse();
    }

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
    public async Task A_bot_is_asked_to_rebase_only_on_a_repository_that_the_settings_name()
    {
        var inner = Substitute.For<IForgeActs>();
        _ = inner.RequestBotRebaseAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var guarded = new GuardedForgeActs(inner, ["example/*"]);

        (await guarded.RequestBotRebaseAsync("other/shop", 7, "@dependabot rebase", Ct)).Should().BeFalse("nothing is written on a repository outside the test bed");
        (await guarded.RequestBotRebaseAsync("example/shop", 7, "@dependabot rebase", Ct)).Should().BeTrue();
        await inner.DidNotReceive().RequestBotRebaseAsync("other/shop", Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
    public async Task A_branch_is_handed_over_by_the_staging_world_of_a_problem_with_no_pull_request_only()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;

        (await HandOverBranchAsync(runner, staging)).StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict, "this problem is a pull request: it is repaired with files, not with a branch");
        factory.Services.GetRequiredService<RepairRegistry>().PendingOf("p-worlds").Should().BeNull();
    }

    [Fact]
    public async Task A_write_of_the_gateway_outside_the_repositories_that_it_may_write_on_is_refused_and_the_inner_gateway_is_never_called()
    {
        var inner = Substitute.For<IForgeActs>();
        var guarded = new GuardedForgeActs(inner, ["orchestrarr-sandbox/*"]);

        var outside = await guarded.OpenPullRequestFromBranchAsync("someone/else", "main", "agent/p-1-abc", new string('a', 40), "t", "b", Ct);
        var inside = await guarded.OpenPullRequestFromBranchAsync("orchestrarr-sandbox/shop-api", "main", "agent/p-1-abc", new string('a', 40), "t", "b", Ct);

        outside.Opened.Should().BeFalse();
        outside.Error.Should().Contain("may not write");
        _ = inside;
        await inner.Received(1).OpenPullRequestFromBranchAsync("orchestrarr-sandbox/shop-api", "main", "agent/p-1-abc", new string('a', 40), "t", "b", Ct);
        await inner.DidNotReceive().OpenPullRequestFromBranchAsync("someone/else", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
    [InlineData("CLAUDE.md")]
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
        // The first mission is already offered to a lane (the runner has not started it yet), and the second problem waits for the first to leave the repository: nothing is offered
        var again = await runner.PostAsJsonAsync("/api/v1/runner/missions/claim?world=Staging", Hello, Ct);
        (await again.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("mission").ValueKind.Should().Be(JsonValueKind.Null);
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

    [Fact]
    public async Task A_free_agent_gets_a_push_credential_for_its_one_repository_and_the_token_is_never_in_the_audit_trail()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartChangeProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;
        (await runner.PostAsync($"/api/v1/runner/missions/{staging}/started", null, Ct)).EnsureSuccessStatusCode();

        var credential = await (await runner.PostAsync($"/api/v1/runner/missions/{staging}/acts/push-credential", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        credential.GetProperty("allowed").GetBoolean().Should().BeTrue(credential.ToString());
        credential.GetProperty("token").GetString().Should().Be($"{FakeForge.PushToken}-1");
        forge.CredentialsMade.Should().Equal([("example/shop", false)], "an operation that is not about its pipeline gets a credential that cannot change a workflow");
        var trail = factory.Services.GetRequiredService<Orchestrarr.Core.Approvals.IAuditLog>().Page(100).Select(row => $"{row.Entry.Event} {row.Entry.Detail}").ToList();
        trail.Should().Contain(line => line.Contains("PushBranch", StringComparison.Ordinal) && line.Contains("not about its pipeline", StringComparison.Ordinal), "the credential and why it may or may not change a workflow are written down");
        trail.Should().NotContain(line => line.Contains(FakeForge.PushToken, StringComparison.Ordinal));

        // A renewal revokes the credential it replaces once the runner placed the new one, never before, and the end of the agent revokes the last one, on the forge itself
        _ = await runner.PostAsync($"/api/v1/runner/missions/{staging}/acts/push-credential", null, Ct);
        forge.Revoked.Should().BeEmpty("a runner that did not get the renewed credential keeps one that works");
        (await runner.PostAsync($"/api/v1/runner/missions/{staging}/acts/push-credential/release?keepLatest=true", null, Ct)).EnsureSuccessStatusCode();
        forge.Revoked.Should().Equal($"{FakeForge.PushToken}-1");
        (await runner.PostAsync($"/api/v1/runner/missions/{staging}/acts/push-credential/release", null, Ct)).EnsureSuccessStatusCode();
        forge.Revoked.Should().Equal($"{FakeForge.PushToken}-1", $"{FakeForge.PushToken}-2");
    }

    [Fact]
    public async Task Engaging_the_global_stop_revokes_on_the_forge_the_push_credentials_that_are_still_valid()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartChangeProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;
        (await runner.PostAsync($"/api/v1/runner/missions/{staging}/started", null, Ct)).EnsureSuccessStatusCode();
        _ = await runner.PostAsync($"/api/v1/runner/missions/{staging}/acts/push-credential", null, Ct);

        _ = await factory.Services.GetRequiredService<IKillSwitchControl>().ChangeAsync(true, new AuditEntry(DateTimeOffset.UtcNow, AuditEvents.KillSwitchEngaged, "alice", null, null), Ct);
        await factory.Services.GetServices<IHostedService>().OfType<MissionDriver>().Single().PassAsync(Ct);

        forge.Revoked.Should().Equal([$"{FakeForge.PushToken}-1"], "deleting the file on the machine is not enough: an agent that copied it could push until it expires");
        factory.Services.GetRequiredService<Orchestrarr.Core.Approvals.IAuditLog>().Page(100).Select(row => row.Entry.Detail ?? string.Empty).Should().Contain(detail => detail.Contains("revoked on the forge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_push_credential_is_made_for_the_staging_of_a_pull_request_or_while_the_global_stop_is_engaged()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartProblemAsync(factory);
        using var runner = Runner(factory);
        var pullRequest = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;
        (await runner.PostAsync($"/api/v1/runner/missions/{pullRequest}/started", null, Ct)).EnsureSuccessStatusCode();

        (await runner.PostAsync($"/api/v1/runner/missions/{pullRequest}/acts/push-credential", null, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound, "the staging of a pull request pushes nothing");

        using var other = new WorldsFactory(forge);
        _ = await StartChangeProblemAsync(other, "p-change");
        using var changeRunner = Runner(other);
        var change = (await ClaimAsync(changeRunner, "Staging")).GetProperty("missionId").GetString()!;
        (await changeRunner.PostAsync($"/api/v1/runner/missions/{change}/started", null, Ct)).EnsureSuccessStatusCode();
        _ = await other.Services.GetRequiredService<IKillSwitchControl>().ChangeAsync(true, new AuditEntry(DateTimeOffset.UtcNow, AuditEvents.KillSwitchEngaged, "alice", null, null), Ct);

        var stopped = await (await changeRunner.PostAsync($"/api/v1/runner/missions/{change}/acts/push-credential", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        stopped.GetProperty("allowed").GetBoolean().Should().BeFalse();
        stopped.GetProperty("refusal").GetString().Should().Contain("stop");
        stopped.GetProperty("token").ValueKind.Should().Be(JsonValueKind.Null);
        forge.CredentialsMade.Should().BeEmpty("nothing is made when the act is not authorised");
    }

    [Fact]
    public async Task A_push_credential_of_a_mission_that_no_longer_runs_is_revoked_at_the_next_pass_of_the_driver()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartChangeProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;
        (await runner.PostAsync($"/api/v1/runner/missions/{staging}/started", null, Ct)).EnsureSuccessStatusCode();
        _ = await runner.PostAsync($"/api/v1/runner/missions/{staging}/acts/push-credential", null, Ct);
        var driver = factory.Services.GetServices<IHostedService>().OfType<MissionDriver>().Single();
        await driver.PassAsync(Ct);
        forge.Revoked.Should().BeEmpty("the mission runs: its credential is the agent's");

        // The runner hands over without saying that the agent is done: the mission no longer runs, and its credential goes with it
        await HandOverBranchAsync(runner, staging, start: false);
        await driver.PassAsync(Ct);

        forge.Revoked.Should().Equal([$"{FakeForge.PushToken}-1"], "a credential never outlives its mission on the forge");
    }

    [Fact]
    public async Task A_revocation_that_the_forge_did_not_confirm_is_tried_again_at_the_next_pass()
    {
        var forge = new FakeForge();
        using var factory = new WorldsFactory(forge);
        _ = await StartChangeProblemAsync(factory);
        using var runner = Runner(factory);
        var staging = (await ClaimAsync(runner, "Staging")).GetProperty("missionId").GetString()!;
        (await runner.PostAsync($"/api/v1/runner/missions/{staging}/started", null, Ct)).EnsureSuccessStatusCode();
        _ = await runner.PostAsync($"/api/v1/runner/missions/{staging}/acts/push-credential", null, Ct);
        _ = await factory.Services.GetRequiredService<IKillSwitchControl>().ChangeAsync(true, new AuditEntry(DateTimeOffset.UtcNow, AuditEvents.KillSwitchEngaged, "alice", null, null), Ct);
        var driver = factory.Services.GetServices<IHostedService>().OfType<MissionDriver>().Single();

        forge.RevokeFails = true;
        await driver.PassAsync(Ct);
        forge.RevokeFails = false;
        await driver.PassAsync(Ct);
        await driver.PassAsync(Ct);

        forge.Revoked.Should().Equal([$"{FakeForge.PushToken}-1", $"{FakeForge.PushToken}-1"], "a forge that was down is asked again, and a credential revoked once is not asked about again");
    }

    [Fact]
    public async Task An_act_on_the_line_that_the_trail_cannot_record_is_undone_and_answered_503()
    {
        using var factory = new WorldsFactory(new FakeForge());
        var starter = factory.Services.GetRequiredService<ProblemStarter>();
        var problems = factory.Services.GetRequiredService<IProblemStore>();
        var trail = factory.Services.GetRequiredService<SwitchableAudit>();
        using var person = await Operator(factory);
        _ = await starter.StartOrQueueAsync(Prepared("p-first", 7, "example/shop"), Ct);
        _ = await starter.StartOrQueueAsync(Prepared("p-held", 8, "example/shop"), Ct);
        async Task<JsonElement> Entry() => (await person.GetFromJsonAsync<JsonElement>("/api/v1/flow/queue", Ct)).GetProperty("line").EnumerateArray().Single(entry => entry.GetProperty("problemId").GetString() == "p-held");

        trail.Writable = false;
        (await person.PostAsJsonAsync("/api/v1/flow/p-held/hold", new { reason = "an analysis" }, Ct)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await Entry()).GetProperty("held").ValueKind.Should().Be(JsonValueKind.Null, "a hold the trail did not take is not made");
        (await person.PostAsJsonAsync("/api/v1/flow/p-held/priority", new { priority = 80 }, Ct)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await Entry()).GetProperty("priority").GetInt32().Should().Be(FlowPriority.Normal);
        (await person.PostAsJsonAsync("/api/v1/flow/p-first/priority", new { priority = 60 }, Ct)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        problems.Get("p-first")!.Priority.Should().Be(FlowPriority.Normal, "a priority in process that the trail did not take is not set");

        trail.Writable = true;
        (await person.PostAsJsonAsync("/api/v1/flow/p-held/hold", new { reason = "an analysis" }, Ct)).EnsureSuccessStatusCode();
        trail.Writable = false;
        (await person.PostAsync("/api/v1/flow/p-held/release", null, Ct)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await Entry()).GetProperty("held").ValueKind.Should().Be(JsonValueKind.Object, "a release the trail did not take is not made");
        problems.Save(problems.Get("p-first")! with { State = ProblemState.Closed });
        await starter.StartReadyAsync(Ct);
        problems.Get("p-held").Should().BeNull("it is still held");
    }

    [Fact]
    public async Task A_held_operation_leaves_the_line_with_its_hold_when_its_approval_runs_out()
    {
        using var factory = new WorldsFactory(new FakeForge());
        var starter = factory.Services.GetRequiredService<ProblemStarter>();
        var line = factory.Services.GetRequiredService<ProblemQueue>();
        var holds = factory.Services.GetRequiredService<ProblemHolds>();
        using var person = await Operator(factory);
        var old = Prepared("p-old", 8, "example/shop").Entry ?? throw new InvalidOperationException("prepared");
        line.Enqueue(new QueuedProblem("row-p-old", old, factory.Services.GetRequiredService<TimeProvider>().GetUtcNow() - ProblemStarter.QueuedLifetime - TimeSpan.FromMinutes(1), Held: new HeldProblem("alice", factory.Services.GetRequiredService<TimeProvider>().GetUtcNow(), "an analysis")));

        await starter.StartReadyAsync(Ct);

        line.Contains("p-old").Should().BeFalse("a hold never keeps an approval alive");
        holds.IsHeld("p-old").Should().BeFalse("the hold leaves with its operation");
        factory.Services.GetRequiredService<IProblemStore>().Get("p-old").Should().BeNull();
        (await person.PostAsync("/api/v1/flow/p-old/release", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task The_traveler_of_an_operation_outside_the_flow_is_read_from_the_store()
    {
        using var factory = new WorldsFactory(new FakeForge());
        var starter = factory.Services.GetRequiredService<ProblemStarter>();
        var problems = factory.Services.GetRequiredService<IProblemStore>();
        using var person = await Operator(factory);
        _ = await starter.StartOrQueueAsync(NoPullRequest("p-old", "example/shop"), Ct);
        var run = problems.Get("p-old")!;
        // its missions are not among the recent ones the flow reads: only the store and the trail know it
        foreach (var mission in factory.Services.GetRequiredService<IMissionStore>().ForProblem("p-old").ToList())
        {
            factory.Services.GetRequiredService<IMissionStore>().Save(mission with { ProblemId = "p-other" });
        }
        problems.Save(run with { State = ProblemState.Closed, EndedAt = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow() });

        var traveler = await person.GetFromJsonAsync<JsonElement>("/api/v1/problems/p-old/traveler", Ct);

        traveler.GetProperty("state").GetString().Should().Be("closed");
        traveler.GetProperty("steps").EnumerateArray().Select(step => step.GetProperty("event").GetString()).Should().Contain("problem.started");
    }
}
