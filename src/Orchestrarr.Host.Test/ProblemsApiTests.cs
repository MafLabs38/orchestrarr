using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Orchestrarr.Host.Test;

/// <summary>The host with the sample fleet, the sample pull requests, the sample descriptions of the applications and the powers to validate and merge.</summary>
public sealed class TableApiFactory : AccountsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Forge:SamplePullRequestsFile", Path.Combine(AppContext.BaseDirectory, "github-pull-requests.sample.json"));
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

    private static object Chosen(JsonElement table, string id, bool stop = false) => new { id, headSha = Row(table, id).GetProperty("headSha").GetString(), stopBeforeProduction = stop };

    [Fact]
    public async Task The_table_says_how_far_each_problem_goes_and_why_it_stops()
    {
        using var factory = new TableApiFactory();
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

    [Fact]
    public async Task Nobody_signed_out_reads_or_validates_and_a_reader_cannot_validate()
    {
        using var factory = new TableApiFactory();
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
        using var operatorClient = await SignedIn(factory, "Operator");
        var table = await Table(operatorClient);

        var answer = await operatorClient.PostAsJsonAsync("/api/v1/problems/approve",
            new { rows = new[] { Chosen(table, "pr:example/shop#2", stop: true), Chosen(table, "pr:example/shop#1"), Chosen(table, "pr:example/web-portal#1") }, secondFactorCode = "123456" }, Ct);

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
        using var operatorClient = await SignedIn(factory, "Operator");
        var table = await Table(operatorClient);

        var answer = await operatorClient.PostAsJsonAsync("/api/v1/problems/approve", new { rows = new[] { Chosen(table, "pr:example/shop#3", stop: true) }, secondFactorCode = "123456" }, Ct);

        answer.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Json(await operatorClient.GetAsync("/api/v1/audit?limit=20", Ct))).EnumerateArray()
            .Should().Contain(entry => (entry.GetProperty("detail").GetString() ?? string.Empty).Contains("stopBeforeProduction=False", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_code_shaped_like_one_nothing_is_queued()
    {
        using var factory = new TableApiFactory();
        using var operatorClient = await SignedIn(factory, "Operator");
        var table = await Table(operatorClient);

        var answer = await operatorClient.PostAsJsonAsync("/api/v1/problems/approve", new { rows = new[] { Chosen(table, "pr:example/shop#2") } }, Ct);

        answer.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(answer)).GetProperty("error").GetString().Should().Be("secondFactorRequired");
        (await Json(await operatorClient.GetAsync("/api/v1/runs", Ct))).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task What_the_client_sends_cannot_widen_a_row_a_row_without_action_a_moved_commit_and_an_unknown_row_are_refused()
    {
        using var factory = new TableApiFactory();
        using var operatorClient = await SignedIn(factory, "Operator");
        var table = await Table(operatorClient);
        var noAction = table.GetProperty("rows").EnumerateArray().First(row => row.GetProperty("goesUpTo").GetString() == "noAction").GetProperty("id").GetString()!;

        foreach (var rows in new object[][]
        {
            [new { id = noAction, headSha = new string('a', 40), stopBeforeProduction = false }],
            [new { id = "pr:example/shop#2", headSha = new string('f', 40), stopBeforeProduction = false }],
            [new { id = "pr:example/shop#99", headSha = new string('a', 40), stopBeforeProduction = false }],
            [new { id = "pr:example/reporting#3", headSha = Row(table, "pr:example/reporting#3").GetProperty("headSha").GetString(), stopBeforeProduction = false }],
        })
        {
            var answer = await operatorClient.PostAsJsonAsync("/api/v1/problems/approve", new { rows, secondFactorCode = "123456" }, Ct);

            answer.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await Json(answer)).GetProperty("error").GetString().Should().Be("planChanged");
        }
        (await Json(await operatorClient.GetAsync("/api/v1/runs", Ct))).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task An_empty_selection_is_refused_and_the_same_selection_twice_is_already_approved()
    {
        using var factory = new TableApiFactory();
        using var operatorClient = await SignedIn(factory, "Operator");
        var table = await Table(operatorClient);

        (await operatorClient.PostAsJsonAsync("/api/v1/problems/approve", new { rows = Array.Empty<object>(), secondFactorCode = "123456" }, Ct)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = new { rows = new[] { Chosen(table, "pr:example/shop#2") }, secondFactorCode = "123456" };
        (await operatorClient.PostAsJsonAsync("/api/v1/problems/approve", body, Ct)).StatusCode.Should().Be(HttpStatusCode.Created);

        var again = await operatorClient.PostAsJsonAsync("/api/v1/problems/approve", body, Ct);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(again)).GetProperty("error").GetString().Should().Be("alreadyApproved");
    }
}
