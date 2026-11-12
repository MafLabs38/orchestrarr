using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrarr.Core.SecondFactor;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Runner;

namespace Orchestrarr.Host.Test;

public sealed class RunnerApiTests : IDisposable
{
    private const string ShopId = "repository:example/shop";
    private static readonly string Token = new('a', 48);
    private static readonly string Head = new('c', 40);
    private static readonly byte[] Secret = [1, 3, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 27, 29, 31, 33, 35, 37, 39];
    private static readonly TotpOptions Shape = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _folder = Directory.CreateTempSubdirectory("orchestrarr-e2e-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private string Journal => Path.Combine(_folder, "journal.jsonl");

    private ForgeApiFactory Factory(bool withRunner = true, bool secondFactor = true) =>
        new()
        {
            Settings = new Dictionary<string, string?>
            {
                ["Tasks:RequireSecondFactor"] = secondFactor ? "true" : "false",
                ["Tasks:JournalPath"] = Journal,
                ["Tasks:Runners:0:Name"] = withRunner ? "runner-1" : string.Empty,
                ["Tasks:Runners:0:Token"] = withRunner ? Token : string.Empty,
            }.Where(pair => withRunner || !pair.Key.StartsWith("Tasks:Runners", StringComparison.Ordinal)).ToDictionary(pair => pair.Key, pair => pair.Value),
        };

    private static async Task<JsonElement> Json(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>(Ct));

    private static Task<HttpResponseMessage> Approve(HttpClient client, string? code = "123456") =>
        client.PostAsJsonAsync("/api/v1/approvals", new { workItemId = ShopId, steps = new[] { "validate", "mergePullRequests" }, pullRequests = new[] { new { number = 7, headSha = Head } }, secondFactorCode = code }, Ct);

    private static HttpClient Runner(ForgeApiFactory factory, string? token = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? Token);
        return client;
    }

    private static readonly RunnerHello Hello = new("runner-1", "1.0", [TaskKind.ValidatePullRequest, TaskKind.MergePullRequest], ["example/*"], []);

    private static string CodeNow() => Totp.Code(Secret, Totp.StepAt(DateTimeOffset.UtcNow, Shape), Shape);

    // --- the routes

    [Fact]
    public async Task A_runner_reads_the_global_stop_while_it_runs_and_only_with_its_token()
    {
        using var factory = Factory();
        var runner = Runner(factory);
        var person = factory.CreateClient();

        (await Json(await runner.GetAsync("/api/v1/runner/stop", Ct))).GetProperty("engaged").GetBoolean().Should().BeFalse();
        (await person.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(await runner.GetAsync("/api/v1/runner/stop", Ct))).GetProperty("engaged").GetBoolean().Should().BeTrue();
        (await Runner(factory, new string('b', 48)).GetAsync("/api/v1/runner/stop", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await person.GetAsync("/api/v1/runner/stop", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a person's session is not a runner's token");
    }

    [Fact]
    public async Task The_real_client_of_the_runner_reads_the_global_stop()
    {
        using var factory = Factory();
        var client = new ControlPlaneClient(factory.CreateClient(), new ControlPlaneOptions { Url = "http://localhost", Token = Token });

        (await client.GlobalStopEngagedAsync(Ct)).Should().BeFalse();
        await factory.CreateClient().PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct);
        (await client.GlobalStopEngagedAsync(Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task With_no_runner_configured_the_routes_are_not_there()
    {
        using var factory = Factory(withRunner: false);

        (await factory.CreateClient().PostAsJsonAsync("/api/v1/runner/claim", new ClaimRequest(Hello), RunnerWire.Json, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_old_route_of_the_executor_is_gone_there_is_no_alias()
    {
        using var factory = Factory(withRunner: true);

        (await factory.CreateClient().PostAsJsonAsync("/api/v1/executor/claim", new ClaimRequest(Hello), RunnerWire.Json, Ct)).StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Unauthorized);
        (await Runner(factory, new string('b', 48)).PostAsJsonAsync("/api/v1/executor/claim", new ClaimRequest(Hello), RunnerWire.Json, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token-wrong-token-wrong-token-wrong")]
    public async Task A_call_without_the_token_of_a_runner_is_refused_and_a_person_cannot_use_the_routes(string? token)
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await client.PostAsJsonAsync("/api/v1/runner/claim", new ClaimRequest(Hello), RunnerWire.Json, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().ContainSingle().Which.Scheme.Should().Be("Bearer");
    }

    [Theory]
    [InlineData("/api/v1/runner/claim")]
    [InlineData("/API/V1/RUNNER/CLAIM")]
    public async Task Nobody_without_a_token_has_a_body_read_and_a_body_without_a_hello_is_refused(string path)
    {
        using var factory = Factory();

        // A body that is not even JSON: without a token it is a 401, never a 400 (the body is not read before the token is checked)
        using var garbage = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");
        (await factory.CreateClient().PostAsync(path, garbage, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // With the token, a claim that does not say who the runner is cannot be recorded
        using var empty = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        (await Runner(factory).PostAsync(path, empty, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await factory.CreateClient().GetAsync("/api/v1/system/status", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_runner_sends_no_header_of_the_interface_and_is_not_asked_for_one()
    {
        using var factory = Factory();
        var client = Runner(factory);
        // The tests add the header of the interface to every request, unless this says not to: a runner never sends it
        client.DefaultRequestHeaders.Add(TestSignInFilter.RealCsrfHeader, "1");

        var response = await client.PostAsJsonAsync("/api/v1/runner/claim", new ClaimRequest(Hello), RunnerWire.Json, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_token_of_a_runner_does_not_open_the_routes_for_people()
    {
        using var factory = Factory();
        var client = Runner(factory);

        // The runner's token says nothing for the routes of the approvals: nobody is signed in, so it is a 401
        client.DefaultRequestHeaders.Add(TestSignInFilter.RoleHeader, "anonymous");

        (await client.PostAsJsonAsync("/api/v1/approvals", new { workItemId = ShopId, steps = new[] { "validate" }, pullRequests = new[] { new { number = 7, headSha = Head } }, secondFactorCode = "123456" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Approving_needs_a_code_shaped_like_one_and_the_status_says_so()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        (await Json(await client.GetAsync("/api/v1/system/status", Ct))).GetProperty("approvals").GetProperty("requiresSecondFactor").GetBoolean().Should().BeTrue();
        var refused = await Approve(client, code: null);
        refused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(refused)).GetProperty("error").GetString().Should().Be("secondFactorRequired");
        (await Approve(client, "12ab56")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(await client.GetAsync("/api/v1/runs", Ct))).GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("100000")]
    [InlineData("abc")]
    public async Task The_page_size_of_the_runs_is_kept_within_its_bounds(string limit)
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        await Approve(client, "123456");

        var answer = await client.GetAsync($"/api/v1/runs?limit={limit}", Ct);

        // A number is clamped to 1..500; text that is not a number is a bad request, never a crash and never everything
        answer.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);
        if (answer.StatusCode == HttpStatusCode.OK)
        {
            (await Json(answer)).GetArrayLength().Should().BeInRange(0, 500);
        }
    }

    [Fact]
    public async Task An_approval_makes_a_waiting_run_whose_tasks_a_person_can_read_and_no_code_appears_anywhere()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        (await Approve(client, "246810")).StatusCode.Should().Be(HttpStatusCode.Created);

        var runs = await Json(await client.GetAsync("/api/v1/runs", Ct));
        var run = runs.EnumerateArray().Should().ContainSingle().Which;
        run.GetProperty("state").GetString().Should().Be("waiting");
        run.GetProperty("taskCount").GetInt32().Should().Be(2);
        var detailText = await (await client.GetAsync($"/api/v1/runs/{run.GetProperty("batchId").GetGuid()}", Ct)).Content.ReadAsStringAsync(Ct);
        detailText.Should().Contain("validatePullRequest").And.Contain("mergePullRequest");
        (detailText + runs.GetRawText() + await (await client.GetAsync("/api/v1/audit", Ct)).Content.ReadAsStringAsync(Ct) + await (await client.GetAsync("/api/v1/approvals", Ct)).Content.ReadAsStringAsync(Ct))
            .Should().NotContain("246810");
        (await client.GetAsync($"/api/v1/runs/{Guid.NewGuid()}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_runner_takes_a_batch_once_reports_its_trace_and_ends_it()
    {
        using var factory = Factory();
        var person = factory.CreateClient();
        await Approve(person, "246810");
        var runner = Runner(factory);

        var claim = await (await runner.PostAsJsonAsync("/api/v1/runner/claim", new ClaimRequest(Hello), RunnerWire.Json, Ct)).Content.ReadFromJsonAsync<ClaimResult>(RunnerWire.Json, Ct);

        claim!.Status.Should().Be(ClaimStatus.Claimed);
        claim.Delivery!.SecondFactorCode.Should().Be("246810");
        var batch = claim.Delivery.Batch;
        (await (await runner.PostAsJsonAsync("/api/v1/runner/claim", new ClaimRequest(Hello), RunnerWire.Json, Ct)).Content.ReadFromJsonAsync<ClaimResult>(RunnerWire.Json, Ct))!.Status.Should().Be(ClaimStatus.Nothing);

        var line = RunEvent.Create(batch.Digest(), 1, DateTimeOffset.UtcNow, RunEvents.Claimed, null, "runner-1");
        (await runner.PostAsJsonAsync($"/api/v1/runner/runs/{batch.Id}/events", new AppendEventsRequest([line with { Detail = "changed" }]), RunnerWire.Json, Ct)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await runner.PostAsJsonAsync($"/api/v1/runner/runs/{batch.Id}/events", new AppendEventsRequest([line]), RunnerWire.Json, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var outcome = new BatchOutcome(BatchState.Done, null, [.. batch.Tasks.Select(task => new TaskOutcome(task.Id, TaskResult.DryRun, "would run"))]);
        (await runner.PostAsJsonAsync($"/api/v1/runner/runs/{batch.Id}/complete", new CompleteRequest(outcome), RunnerWire.Json, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await runner.PostAsJsonAsync($"/api/v1/runner/runs/{batch.Id}/complete", new CompleteRequest(outcome), RunnerWire.Json, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await runner.PostAsJsonAsync($"/api/v1/runner/runs/{Guid.NewGuid()}/complete", new CompleteRequest(outcome), RunnerWire.Json, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var run = await Json(await person.GetAsync($"/api/v1/runs/{batch.Id}", Ct));
        run.GetProperty("summary").GetProperty("state").GetString().Should().Be("done");
        run.GetProperty("events").GetArrayLength().Should().Be(1);
        (await Json(await person.GetAsync("/api/v1/approvals", Ct))).EnumerateArray().Should().OnlyContain(a => a.GetProperty("state").GetString() == "ended");
        (await Approve(person)).StatusCode.Should().Be(HttpStatusCode.Created, "a dry run spends the approval: it may be given again");
    }

    [Fact]
    public async Task The_status_shows_the_runners_that_asked_for_work()
    {
        using var factory = Factory();
        await Runner(factory).PostAsJsonAsync("/api/v1/runner/claim", new ClaimRequest(Hello), RunnerWire.Json, Ct);

        var runners = (await Json(await factory.CreateClient().GetAsync("/api/v1/system/status", Ct))).GetProperty("runners");

        var seen = runners.EnumerateArray().Should().ContainSingle().Which;
        seen.GetProperty("name").GetString().Should().Be("runner-1");
        seen.GetProperty("connected").GetBoolean().Should().BeTrue();
        seen.GetProperty("repositories").EnumerateArray().Select(r => r.GetString()).Should().Equal("example/*");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_status_shows_whether_a_runner_checks_the_code_as_it_said_in_its_hello(bool checksTheCode)
    {
        using var factory = Factory();
        await Runner(factory).PostAsJsonAsync("/api/v1/runner/claim", new ClaimRequest(Hello with { SecondFactorRequired = checksTheCode }), RunnerWire.Json, Ct);

        var runners = (await Json(await factory.CreateClient().GetAsync("/api/v1/system/status", Ct))).GetProperty("runners");

        runners.EnumerateArray().Single().GetProperty("secondFactorRequired").GetBoolean().Should().Be(checksTheCode);
    }

    // --- the real runner against the real control plane

    private (RunnerStore Store, RunnerWorker Worker) RealRunner(ForgeApiFactory factory, string name = "runner-1")
    {
        var store = new RunnerStore(Path.Combine(_folder, "runner.db"));
        store.Enrol("test-operator", Secret, 0, DateTimeOffset.UtcNow.AddMinutes(-5));
        var rights = new RightsOptions { Kinds = [TaskKind.ValidatePullRequest, TaskKind.MergePullRequest], Repositories = ["example/*"] };
        var pipeline = new RunnerPipeline(
            store,
            new SecondFactorGuard(store, store, TimeProvider.System, new SecondFactorOptions()),
            new FileApprovalJournal(Journal),
            rights,
            [new DryRunTaskHandler(TaskKind.ValidatePullRequest), new DryRunTaskHandler(TaskKind.MergePullRequest)],
            TimeProvider.System,
            name);
        var http = factory.CreateClient();
        var controlPlane = new ControlPlaneClient(http, new ControlPlaneOptions { Url = http.BaseAddress!.ToString(), Token = Token });
        return (store, new RunnerWorker(controlPlane, pipeline, store, new RunnerOptions { Name = name, Rights = rights }, TimeProvider.System, NullLogger<RunnerWorker>.Instance));
    }

    [Fact]
    public async Task The_real_runner_checks_the_code_and_the_journal_and_the_run_shows_in_the_control_plane()
    {
        using var factory = Factory();
        var person = factory.CreateClient();
        var (_, worker) = RealRunner(factory);
        (await Approve(person, CodeNow())).StatusCode.Should().Be(HttpStatusCode.Created);

        (await worker.StepAsync(Ct)).Should().BeTrue();

        var runs = await Json(await person.GetAsync("/api/v1/runs", Ct));
        var summary = runs.EnumerateArray().Should().ContainSingle().Which;
        summary.GetProperty("state").GetString().Should().Be("done");
        summary.GetProperty("runner").GetString().Should().Be("runner-1");
        var detail = await Json(await person.GetAsync($"/api/v1/runs/{summary.GetProperty("batchId").GetGuid()}", Ct));
        detail.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("type").GetString()).Should().Equal(
            "run.claimed", "run.batch-checked", "run.double-control", "run.rights", "run.second-factor", "task.dry-run", "task.dry-run", "run.completed");
        detail.GetProperty("tasks").EnumerateArray().Should().OnlyContain(task => task.GetProperty("result").GetString() == "dryRun");
        (await Json(await person.GetAsync("/api/v1/audit", Ct))).EnumerateArray().Select(e => e.GetProperty("event").GetString())
            .Should().Contain(["batch.queued", "batch.delivered", "batch.done"]);
    }

    [Fact]
    public async Task A_wrong_code_ends_the_run_refused_and_the_operator_sees_why_without_the_code_leaking()
    {
        using var factory = Factory();
        var person = factory.CreateClient();
        var (_, worker) = RealRunner(factory);
        var right = CodeNow();
        var wrong = right == "000000" ? "000001" : "000000";
        await Approve(person, wrong);

        await worker.StepAsync(Ct);

        var summary = (await Json(await person.GetAsync("/api/v1/runs", Ct))).EnumerateArray().Single();
        summary.GetProperty("state").GetString().Should().Be("refused");
        summary.GetProperty("refusal").GetString().Should().Be("secondFactor");
        var detail = await (await person.GetAsync($"/api/v1/runs/{summary.GetProperty("batchId").GetGuid()}", Ct)).Content.ReadAsStringAsync(Ct);
        detail.Should().Contain("refused: Wrong").And.NotContain(wrong).And.NotContain(right);
        (await Json(await person.GetAsync("/api/v1/approvals", Ct))).EnumerateArray().Should().OnlyContain(a => a.GetProperty("state").GetString() == "refused");
    }

    [Fact]
    public async Task A_batch_that_the_journal_does_not_hold_is_refused_by_the_real_runner()
    {
        using var factory = Factory();
        var person = factory.CreateClient();
        var (_, worker) = RealRunner(factory);
        await Approve(person, CodeNow());
        File.Delete(Journal);

        await worker.StepAsync(Ct);

        (await Json(await person.GetAsync("/api/v1/runs", Ct))).EnumerateArray().Single().GetProperty("refusal").GetString().Should().Be("journalMismatch");
    }
}
