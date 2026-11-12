using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Host.Test;

/// <summary>The host with the sample fleet, the sample pull requests, the sample descriptions of the applications and the powers to validate and merge.</summary>
public sealed class TableApiFactory : AccountsApiFactory
{
    public static readonly string RunnerToken = new('t', 48);

    /// <summary>The runner says what it accepts, as the real one does when it asks for work: the table promises nothing more.</summary>
    public async Task ConnectRunnerAsync(params TaskKind[] kinds)
    {
        using var runner = CreateClient();
        runner.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", RunnerToken);
        var hello = new RunnerHello("runner-1", "1.0", kinds.Length == 0 ? [TaskKind.ValidatePullRequest, TaskKind.MergePullRequest] : kinds, ["example/*"], []);
        (await runner.PostAsJsonAsync("/api/v1/runner/claim", new ClaimRequest(hello), RunnerWire.Json, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Forge:SamplePullRequestsFile", Path.Combine(AppContext.BaseDirectory, "github-pull-requests.sample.json"));
        builder.UseSetting("Tasks:Runners:0:Name", "runner-1");
        builder.UseSetting("Tasks:Runners:0:Token", RunnerToken);
        builder.UseSetting("Applications:File", Path.Combine(AppContext.BaseDirectory, "applications.sample.json"));
        // Outside Development a grant on every repository may only validate: the repositories are named
        string[] repositories = ["example/shop", "example/web-portal", "example/reporting"];
        for (var i = 0; i < repositories.Length; i++)
        {
            builder.UseSetting($"Powers:Grants:{i}:Repository", repositories[i]);
            builder.UseSetting($"Powers:Grants:{i}:Steps:0", "validate");
            builder.UseSetting($"Powers:Grants:{i}:Steps:1", "mergePullRequests");
        }
    }
}

public sealed class ProblemsApiTests
{
    private const string Header = "X-Orchestrarr-Request";
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static async Task<HttpClient> SignedIn(TableApiFactory factory, string role)
    {
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add(Header, "test");
        (await admin.PostAsJsonAsync("/api/v1/auth/login", new { name = AccountsApiFactory.AdminName, password = AccountsApiFactory.AdminPassword }, Ct)).EnsureSuccessStatusCode();
        var name = $"{role.ToLowerInvariant()}-{Guid.NewGuid().ToString("N")[..8]}";
        var password = $"password of {name}";
        (await admin.PostAsJsonAsync("/api/v1/accounts", new { name, role, password }, Ct)).StatusCode.Should().Be(HttpStatusCode.Created);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Header, "test");
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { name, password }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    // The table is read once the pull requests of the sample forge are in
    private static async Task<JsonElement> Table(HttpClient client)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var table = await Json(await client.GetAsync("/api/v1/problems", Ct));
            if (table.GetProperty("rows").EnumerateArray().All(row => row.GetProperty("kind").GetString() != "pullRequestsNotRead"))
            {
                return table;
            }
            await Task.Delay(100, Ct);
        }
        throw new InvalidOperationException("The pull requests were never read.");
    }

    private static JsonElement Row(JsonElement table, string id) => table.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("id").GetString() == id);

    private static object Chosen(JsonElement table, string id, bool stop = false) =>
        new { id, headSha = Row(table, id).GetProperty("headSha").GetString(), goesUpTo = Row(table, id).GetProperty("goesUpTo").GetString(), stopBeforeProduction = stop };

    private static string Version(JsonElement table) => table.GetProperty("descriptionsVersion").GetString()!;

    [Fact]
    public async Task The_table_says_how_far_each_problem_goes_and_why_it_stops()
    {
        using var factory = new TableApiFactory();
        await factory.ConnectRunnerAsync();
        using var reader = await SignedIn(factory, "Reader");

        var table = await Table(reader);

        table.GetProperty("descriptionsVersion").GetString().Should().HaveLength(12);
        Row(table, "pr:example/shop#2").GetProperty("goesUpTo").GetString().Should().Be("production");
        Row(table, "pr:example/shop#2").GetProperty("canStopBeforeProduction").GetBoolean().Should().BeTrue();
        var failing = Row(table, "pr:example/shop#1");
        failing.GetProperty("goesUpTo").GetString().Should().Be("qualification");
        failing.GetProperty("why").GetString().Should().Be("checksFailing");
        var major = Row(table, "pr:example/shop#3");
        major.GetProperty("goesUpTo").GetString().Should().Be("mergeIntoDevelop");
        major.GetProperty("why").GetString().Should().Be("updateClassStopsBeforeProduction");
        Row(table, "pr:example/reporting#3").GetProperty("why").GetString().Should().Be("noDescription");
        table.GetProperty("rows").EnumerateArray().Where(row => row.GetProperty("kind").GetString() == "exposedSecret").Should().OnlyContain(row => row.GetProperty("goesUpTo").GetString() == "noAction");
    }

    private static object Skipped(JsonElement table, string id) => new { id, description = Row(table, id).GetProperty("description").GetString() };

    [Fact]
    public async Task A_row_set_aside_leaves_the_table_and_the_history_keeps_it_until_it_is_taken_back()
    {
        using var factory = new TableApiFactory();
        await factory.ConnectRunnerAsync();
        using var operatorClient = await SignedIn(factory, "Operator");
        var table = await Table(operatorClient);

        var skip = await operatorClient.PostAsJsonAsync("/api/v1/problems/skip", new { rows = new[] { Skipped(table, "pr:example/shop#3") }, reason = "major, not now" }, Ct);

        skip.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await Table(operatorClient);
        after.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("id").GetString()).Should().NotContain("pr:example/shop#3");
        after.GetProperty("skipped").GetInt32().Should().Be(1);
        var historyAnswer = await operatorClient.GetAsync("/api/v1/problems/history?state=skipped", Ct);
        historyAnswer.StatusCode.Should().Be(HttpStatusCode.OK, await historyAnswer.Content.ReadAsStringAsync(Ct));
        var history = await Json(historyAnswer);
        var line = history.GetProperty("lines").EnumerateArray().Single();
        line.GetProperty("state").GetString().Should().Be("skipped");
        line.GetProperty("outcome").GetString().Should().Be("skipped");
        line.GetProperty("reason").GetString().Should().Be("major, not now");
        line.GetProperty("application").GetString().Should().Be("example/shop");

        (await operatorClient.PostAsJsonAsync("/api/v1/problems/unskip", new { id = line.GetProperty("id").GetInt64() }, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Table(operatorClient)).GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("id").GetString()).Should().Contain("pr:example/shop#3");
        (await Json(await operatorClient.GetAsync("/api/v1/problems/history", Ct))).GetProperty("lines").GetArrayLength().Should().Be(0);
        (await operatorClient.PostAsJsonAsync("/api/v1/problems/unskip", new { id = line.GetProperty("id").GetInt64() }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_skip_is_for_what_the_operator_saw_and_only_an_operator_can_make_it()
    {
        using var factory = new TableApiFactory();
        await factory.ConnectRunnerAsync();
        using var operatorClient = await SignedIn(factory, "Operator");
        using var reader = await SignedIn(factory, "Reader");
        var table = await Table(operatorClient);

        (await operatorClient.PostAsJsonAsync("/api/v1/problems/skip", new { rows = new[] { new { id = "pr:example/shop#3", description = "something else" } } }, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await operatorClient.PostAsJsonAsync("/api/v1/problems/skip", new { rows = new[] { new { id = "pr:example/gone#9", description = "x" } } }, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await operatorClient.PostAsJsonAsync("/api/v1/problems/skip", new { rows = Array.Empty<object>() }, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await reader.PostAsJsonAsync("/api/v1/problems/skip", new { rows = new[] { Skipped(table, "pr:example/shop#3") } }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.GetAsync("/api/v1/problems/history", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "reading the history needs only the right to read");
        (await Table(operatorClient)).GetProperty("skipped").GetInt32().Should().Be(0);
    }

        [Fact]
    public async Task Nobody_signed_out_reads_the_history_or_sets_a_row_aside_and_a_reader_cannot_take_a_skip_back()
    {
        using var factory = new TableApiFactory();
        await factory.ConnectRunnerAsync();
        using var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Orchestrarr-Request", "test");
        using var reader = await SignedIn(factory, "Reader");

        (await anonymous.GetAsync("/api/v1/problems/history", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/v1/problems/skip", new { rows = Array.Empty<object>() }, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await reader.PostAsJsonAsync("/api/v1/problems/unskip", new { id = 1 }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("state=7")]
    [InlineData("state=nonsense")]
    public async Task A_state_that_is_not_one_of_the_two_is_refused_and_the_page_size_is_bounded(string query)
    {
        using var factory = new TableApiFactory();
        await factory.ConnectRunnerAsync();
        using var reader = await SignedIn(factory, "Reader");

        (await reader.GetAsync($"/api/v1/problems/history?{query}", Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await reader.GetAsync("/api/v1/problems/history?limit=0", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.GetAsync("/api/v1/problems/history?limit=100000", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_approved_row_is_marked_in_progress_and_the_history_keeps_its_photograph_with_the_outcome()
    {
        using var factory = new TableApiFactory();
        await factory.ConnectRunnerAsync();
        using var operatorClient = await SignedIn(factory, "Operator");
        var table = await Table(operatorClient);

        (await operatorClient.PostAsJsonAsync("/api/v1/problems/approve", new { rows = new[] { Chosen(table, "pr:example/shop#2") }, secondFactorCode = "123456", descriptionsVersion = Version(table) }, Ct)).StatusCode.Should().Be(HttpStatusCode.Created);

        var after = await Table(operatorClient);
        Row(after, "pr:example/shop#2").GetProperty("inProgress").GetBoolean().Should().BeTrue();
        Row(after, "pr:example/shop#2").GetProperty("tab").GetString().Should().Be("act");
        var line = (await Json(await operatorClient.GetAsync("/api/v1/problems/history?state=approved", Ct))).GetProperty("lines").EnumerateArray().Single();
        line.GetProperty("outcome").GetString().Should().Be("inProgress");
        line.GetProperty("description").GetString().Should().Be(Row(table, "pr:example/shop#2").GetProperty("description").GetString());
        line.GetProperty("pullRequest").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Every_row_says_in_which_tab_it_belongs()
    {
        using var factory = new TableApiFactory();
        await factory.ConnectRunnerAsync();
        using var reader = await SignedIn(factory, "Reader");

        var table = await Table(reader);

        Row(table, "pr:example/shop#2").GetProperty("tab").GetString().Should().Be("act");
        Row(table, "pr:example/reporting#3").GetProperty("tab").GetString().Should().Be("configure", "no description of the application is something to configure");
    }

    [Fact]
    public async Task Nobody_signed_out_reads_or_validates_and_a_reader_cannot_validate()
    {
        using var factory = new TableApiFactory();
        await factory.ConnectRunnerAsync();
        using var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add(Header, "test");
        using var reader = await SignedIn(factory, "Reader");

        (await anonymous.GetAsync("/api/v1/problems", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/v1/problems/approve", new { rows = Array.Empty<object>() }, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await reader.PostAsJsonAsync("/api/v1/problems/approve", new { rows = Array.Empty<object>(), secondFactorCode = "123456" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_selection_of_two_applications_is_approved_with_one_code_as_one_run_and_the_trail_keeps_the_plan()
    {
        using var factory = new TableApiFactory();
        await factory.ConnectRunnerAsync();
        using var operatorClient = await SignedIn(factory, "Operator");
        var table = await Table(operatorClient);

        var answer = await operatorClient.PostAsJsonAsync("/api/v1/problems/approve",
            new { rows = new[] { Chosen(table, "pr:example/shop#2", stop: true), Chosen(table, "pr:example/shop#1"), Chosen(table, "pr:example/web-portal#1") }, secondFactorCode = "123456", descriptionsVersion = Version(table) }, Ct);

        answer.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Json(answer)).GetProperty("approvals").GetArrayLength().Should().Be(3);
        var runs = await Json(await operatorClient.GetAsync("/api/v1/runs", Ct));
        var run = runs.EnumerateArray().Should().ContainSingle("one code, one batch").Which;
        run.GetProperty("taskCount").GetInt32().Should().Be(5, "shop#2 and web-portal#1 validate and merge, shop#1 only validates");
        var trail = (await Json(await operatorClient.GetAsync("/api/v1/audit?limit=50", Ct))).EnumerateArray().Select(entry => entry.GetProperty("detail").GetString() ?? string.Empty).ToList();
        trail.Should().Contain(detail => detail.Contains("example/shop#2", StringComparison.Ordinal) && detail.Contains("stopBeforeProduction=True", StringComparison.Ordinal) && detail.Contains("goesUpTo=Production", StringComparison.Ordinal));
        trail.Should().Contain(detail => detail.Contains("example/shop#1", StringComparison.Ordinal) && detail.Contains("goesUpTo=Qualification", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stop_asked_for_a_row_that_cannot_reach_production_is_not_recorded_as_one()
    {
        using var factory = new TableApiFactory();
        await factory.ConnectRunnerAsync();
        using var operatorClient = await SignedIn(factory, "Operator");
        var table = await Table(operatorClient);

        var answer = await operatorClient.PostAsJsonAsync("/api/v1/problems/approve", new { rows = new[] { Chosen(table, "pr:example/shop#3", stop: true) }, secondFactorCode = "123456", descriptionsVersion = Version(table) }, Ct);

        answer.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Json(await operatorClient.GetAsync("/api/v1/audit?limit=20", Ct))).EnumerateArray()
            .Should().Contain(entry => (entry.GetProperty("detail").GetString() ?? string.Empty).Contains("stopBeforeProduction=False", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_code_shaped_like_one_nothing_is_queued()
    {
        using var factory = new TableApiFactory();
        await factory.ConnectRunnerAsync();
        using var operatorClient = await SignedIn(factory, "Operator");
        var table = await Table(operatorClient);

        var answer = await operatorClient.PostAsJsonAsync("/api/v1/problems/approve", new { rows = new[] { Chosen(table, "pr:example/shop#2") }, descriptionsVersion = Version(table) }, Ct);

        answer.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(answer)).GetProperty("error").GetString().Should().Be("secondFactorRequired");
        (await Json(await operatorClient.GetAsync("/api/v1/runs", Ct))).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task What_the_client_sends_cannot_widen_a_row_a_row_without_action_a_moved_commit_and_an_unknown_row_are_refused()
    {
        using var factory = new TableApiFactory();
        await factory.ConnectRunnerAsync();
        using var operatorClient = await SignedIn(factory, "Operator");
        var table = await Table(operatorClient);
        var noAction = table.GetProperty("rows").EnumerateArray().First(row => row.GetProperty("goesUpTo").GetString() == "noAction").GetProperty("id").GetString()!;

        foreach (var rows in new object[][]
        {
            [new { id = noAction, headSha = new string('a', 40), goesUpTo = "production", stopBeforeProduction = false }],
            [new { id = "pr:example/shop#2", headSha = new string('f', 40), goesUpTo = "production", stopBeforeProduction = false }],
            [new { id = "pr:example/shop#99", headSha = new string('a', 40), goesUpTo = "production", stopBeforeProduction = false }],
            [new { id = "pr:example/reporting#3", headSha = Row(table, "pr:example/reporting#3").GetProperty("headSha").GetString(), goesUpTo = "production", stopBeforeProduction = false }],
        })
        {
            var answer = await operatorClient.PostAsJsonAsync("/api/v1/problems/approve", new { rows, secondFactorCode = "123456", descriptionsVersion = Version(table) }, Ct);

            answer.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await Json(answer)).GetProperty("error").GetString().Should().Be("planChanged");
        }
        (await Json(await operatorClient.GetAsync("/api/v1/runs", Ct))).GetArrayLength().Should().Be(0);
        (await Json(await operatorClient.GetAsync("/api/v1/audit?limit=20", Ct))).EnumerateArray()
            .Should().Contain(entry => entry.GetProperty("event").GetString() == "approval.refused" && (entry.GetProperty("detail").GetString() ?? string.Empty).Contains("PlanChanged", StringComparison.Ordinal),
                "a refusal decided before the service is traced too");
    }

    [Fact]
    public async Task The_approval_is_for_how_far_the_row_went_when_it_was_seen_and_for_the_descriptions_of_that_moment()
    {
        using var factory = new TableApiFactory();
        await factory.ConnectRunnerAsync();
        using var operatorClient = await SignedIn(factory, "Operator");
        var table = await Table(operatorClient);
        var row = Row(table, "pr:example/shop#2");

        var higher = await operatorClient.PostAsJsonAsync("/api/v1/problems/approve",
            new { rows = new[] { new { id = "pr:example/shop#2", headSha = row.GetProperty("headSha").GetString(), goesUpTo = "qualification", stopBeforeProduction = false } }, secondFactorCode = "123456", descriptionsVersion = Version(table) }, Ct);
        var otherDescriptions = await operatorClient.PostAsJsonAsync("/api/v1/problems/approve",
            new { rows = new[] { Chosen(table, "pr:example/shop#2") }, secondFactorCode = "123456", descriptionsVersion = "000000000000" }, Ct);
        var noCeiling = await operatorClient.PostAsJsonAsync("/api/v1/problems/approve",
            new { rows = new[] { new { id = "pr:example/shop#2", headSha = row.GetProperty("headSha").GetString(), stopBeforeProduction = false } }, secondFactorCode = "123456", descriptionsVersion = Version(table) }, Ct);

        higher.StatusCode.Should().Be(HttpStatusCode.Conflict, "the row goes higher now than the operator saw");
        otherDescriptions.StatusCode.Should().Be(HttpStatusCode.Conflict);
        noCeiling.StatusCode.Should().Be(HttpStatusCode.Conflict, "a request that does not say what was seen approves nothing");
        (await Json(await operatorClient.GetAsync("/api/v1/runs", Ct))).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task With_no_runner_connected_nothing_can_be_done_and_the_table_says_why()
    {
        using var factory = new TableApiFactory();
        using var reader = await SignedIn(factory, "Reader");

        var table = await Table(reader);

        foreach (var id in new[] { "pr:example/shop#2", "pr:example/web-portal#1" })
        {
            Row(table, id).GetProperty("goesUpTo").GetString().Should().Be("noAction");
            Row(table, id).GetProperty("why").GetString().Should().Be("noRunner");
        }
    }

    [Fact]
    public async Task A_runner_that_only_validates_keeps_every_row_at_the_qualification()
    {
        using var factory = new TableApiFactory();
        await factory.ConnectRunnerAsync(TaskKind.ValidatePullRequest);
        using var reader = await SignedIn(factory, "Reader");

        var table = await Table(reader);

        Row(table, "pr:example/shop#2").GetProperty("goesUpTo").GetString().Should().Be("qualification");
        Row(table, "pr:example/shop#2").GetProperty("why").GetString().Should().Be("noRunner");
    }

    [Fact]
    public async Task An_empty_selection_is_refused_and_the_same_selection_twice_is_already_approved()
    {
        using var factory = new TableApiFactory();
        await factory.ConnectRunnerAsync();
        using var operatorClient = await SignedIn(factory, "Operator");
        var table = await Table(operatorClient);

        (await operatorClient.PostAsJsonAsync("/api/v1/problems/approve", new { rows = Array.Empty<object>(), secondFactorCode = "123456" }, Ct)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = new { rows = new[] { Chosen(table, "pr:example/shop#2") }, secondFactorCode = "123456", descriptionsVersion = Version(table) };
        (await operatorClient.PostAsJsonAsync("/api/v1/problems/approve", body, Ct)).StatusCode.Should().Be(HttpStatusCode.Created);

        var again = await operatorClient.PostAsJsonAsync("/api/v1/problems/approve", body, Ct);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(again)).GetProperty("error").GetString().Should().Be("alreadyApproved");
    }
}
