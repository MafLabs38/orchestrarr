using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Host.Approvals;

namespace Orchestrarr.Host.Test;

/// <summary>The sample fleet in the Testing environment: no grant, which is what a production host starts as.</summary>
public sealed class NoGrantApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:Provider", "Memory");
        builder.UseSetting(FleetSourceFactory.SampleFileKey, Path.Combine(AppContext.BaseDirectory, "portarr-maintenance.sample.json"));
        builder.ConfigureTestServices(services => services.AddTestSignIn());
    }
}

/// <summary>A queue with no room left.</summary>
internal sealed class FullQueue : IApprovalStore
{
    public int Free => 0;

    public bool Contains(Approval approval) => false;

    public IReadOnlyList<Approval> List(int limit = int.MaxValue) => [.. Array.Empty<Approval>().Take(limit)];
}

/// <summary>An identity chosen by the caller of the test, through a header: several people at once.</summary>
internal sealed class HeaderApprover : IApproverAccessor
{
    public Approver? Current(HttpContext context) =>
        context.Request.Headers.TryGetValue("X-Test-User", out var user) ? new Approver(user.ToString(), IsPerson: true, IsOperator: true, StepUpAt: null) : null;

    public string? DisplayName(HttpContext context) => Current(context)?.Subject;
}

/// <summary>An identity that the test chooses: nobody, or somebody who is not an operator.</summary>
internal sealed class FixedApprover(Approver? approver) : IApproverAccessor
{
    public Approver? Current(HttpContext context) => approver;

    public string? DisplayName(HttpContext context) => approver?.Subject;
}

public sealed class ApprovalsApiTests
{
    private const string Shop = "repository:example/shop";
    private static readonly string Head = new('c', 40);
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static object Seen(string? head = null) => new[] { new { number = 7, headSha = head ?? Head } };

    private static Task<HttpResponseMessage> Approve(HttpClient client, string workItemId = Shop, string[]? steps = null, object? seen = null) =>
        client.PostAsJsonAsync("/api/v1/approvals", new { workItemId, steps = steps ?? ["validate", "mergePullRequests"], pullRequests = seen ?? Seen() }, Ct);

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static Task<HttpResponseMessage> Stop(HttpClient client, bool engaged) =>
        client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged }, Ct);

    private static void Replace<T>(IServiceCollection services, T instance)
        where T : class
    {
        services.RemoveAll<T>();
        services.AddSingleton(instance);
    }

    // A trail that holds nothing: every write is refused
    private static void FullTrail(IServiceCollection services)
    {
        var full = new InMemoryAuditLog(capacity: 0);
        Replace<IAuditLog>(services, full);
        Replace<IAuditSink>(services, full);
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response) => (await Json(response)).GetProperty("error").GetString()!;

    // --- the contract

    [Fact]
    public async Task The_status_says_who_may_approve_and_whether_the_stop_is_engaged()
    {
        using var factory = new ForgeApiFactory();

        var approvals = (await Json(await factory.CreateClient().GetAsync("/api/v1/system/status", Ct))).GetProperty("approvals");

        approvals.GetProperty("operator").GetString().Should().Be("test-operator");
        approvals.GetProperty("operatorName").GetString().Should().Be("test-operator");
        approvals.GetProperty("isOperator").GetBoolean().Should().BeTrue();
        approvals.GetProperty("killSwitchEngaged").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Approving_records_one_approval_bound_to_the_commit_the_server_read_and_leaves_a_trace()
    {
        using var factory = new ForgeApiFactory();
        var client = factory.CreateClient();

        var response = await Approve(client);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var approval = (await Json(response)).GetProperty("approvals").EnumerateArray().Should().ContainSingle().Which;
        approval.GetProperty("repository").GetString().Should().Be("example/shop");
        approval.GetProperty("pullRequest").GetInt32().Should().Be(7);
        approval.GetProperty("headSha").GetString().Should().Be(Head);
        approval.GetProperty("approver").GetString().Should().Be("test-operator");
        approval.GetProperty("steps").EnumerateArray().Select(s => s.GetString()).Should().Equal("validate", "mergePullRequests");

        (await Json(await client.GetAsync("/api/v1/approvals", Ct))).EnumerateArray().Should().ContainSingle();
        var trail = (await Json(await client.GetAsync("/api/v1/audit", Ct))).EnumerateArray().ToList();
        trail.Select(entry => entry.GetProperty("event").GetString()).Should().BeEquivalentTo("approval.recorded", "batch.queued");
        trail.Should().OnlyContain(entry => entry.GetProperty("subject").GetString() == "test-operator");
        approval.GetProperty("state").GetString().Should().Be("waiting");
    }

    [Fact]
    public async Task The_client_cannot_choose_the_commit_the_repository_or_the_approver()
    {
        using var factory = new ForgeApiFactory();
        var client = factory.CreateClient();
        var body = new StringContent(
            $$"""{"workItemId":"{{Shop}}","steps":["validate"],"pullRequests":[{"number":7,"headSha":"{{Head}}"}],"headSha":"{{new string('f', 40)}}","repository":"evil/repo","approver":"mallory"}""",
            Encoding.UTF8, "application/json");

        var approval = (await Json(await client.PostAsync("/api/v1/approvals", body, Ct))).GetProperty("approvals")[0];

        approval.GetProperty("headSha").GetString().Should().Be(Head);
        approval.GetProperty("repository").GetString().Should().Be("example/shop");
        approval.GetProperty("approver").GetString().Should().Be("test-operator");
    }

    [Fact]
    public async Task What_the_operator_saw_must_be_what_is_there()
    {
        using var factory = new ForgeApiFactory();
        var client = factory.CreateClient();

        var pushed = await Approve(client, seen: Seen(new string('f', 40)));
        var nothingSeen = await Approve(client, seen: Array.Empty<object>());
        var noPullRequestsAtAll = await client.PostAsJsonAsync("/api/v1/approvals", new { workItemId = Shop, steps = new[] { "validate" } }, Ct);

        pushed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorOf(pushed)).Should().Be("planChanged");
        (await ErrorOf(nothingSeen)).Should().Be("planChanged");
        (await ErrorOf(noPullRequestsAtAll)).Should().Be("planChanged");
        (await Json(await client.GetAsync("/api/v1/approvals", Ct))).EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_approval_twice_is_a_conflict()
    {
        using var factory = new ForgeApiFactory();
        var client = factory.CreateClient();
        await Approve(client);

        var again = await Approve(client);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorOf(again)).Should().Be("alreadyApproved");
    }

    [Theory]
    [InlineData("repository:example/nothing", "validate", HttpStatusCode.NotFound, "unknownWorkItem")]
    [InlineData(Shop, "mergePullRequests", HttpStatusCode.UnprocessableEntity, "mergeNeedsValidation")]
    [InlineData(Shop, "deploy", HttpStatusCode.UnprocessableEntity, "stepNotOffered")]
    [InlineData(Shop, "", HttpStatusCode.UnprocessableEntity, "nothingTicked")]
    public async Task A_refusal_is_a_status_and_a_code_never_a_sentence(string id, string step, HttpStatusCode status, string code)
    {
        using var factory = new ForgeApiFactory();

        var response = await Approve(factory.CreateClient(), id, step.Length == 0 ? [] : [step]);

        var body = await Json(response);
        response.StatusCode.Should().Be(status);
        body.EnumerateObject().Select(p => p.Name).Should().Equal("error");
        body.GetProperty("error").GetString().Should().Be(code);
    }

    [Fact]
    public async Task A_full_queue_and_an_unwritable_trail_are_a_503()
    {
        using var queue = new ForgeApiFactory { ServiceOverrides = services => Replace<IApprovalStore>(services, new FullQueue()) };
        using var trail = new ForgeApiFactory { ServiceOverrides = FullTrail };

        var full = await Approve(queue.CreateClient());
        var unwritable = await Approve(trail.CreateClient());

        full.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ErrorOf(full)).Should().Be("storeFull");
        unwritable.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ErrorOf(unwritable)).Should().Be("auditUnavailable");
    }

    [Fact]
    public async Task A_pull_request_list_that_is_not_read_is_a_conflict()
    {
        using var factory = new ForgeApiFactory { Failure = () => new HttpRequestException("down") };

        var response = await Approve(factory.CreateClient());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorOf(response)).Should().Be("pullRequestsNotRead");
    }

    // --- what a client may send

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"workItemId":"x","steps":["bogus"],"pullRequests":[]}""")]
    [InlineData("""{"workItemId":"x","steps":[99],"pullRequests":[{"number":"seven","headSha":1}]}""")]
    public async Task A_body_that_cannot_be_read_is_a_400_and_records_nothing(string body)
    {
        using var factory = new ForgeApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/api/v1/approvals", new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(await client.GetAsync("/api/v1/approvals", Ct))).EnumerateArray().Should().BeEmpty();
    }

    [Theory]
    [InlineData("{}", "unknownWorkItem")]
    [InlineData("""{"workItemId":null,"steps":null,"pullRequests":null}""", "unknownWorkItem")]
    [InlineData("""{"workItemId":"repository:example/shop","steps":null,"pullRequests":null}""", "nothingTicked")]
    [InlineData("""{"workItemId":"repository:evil\nforged","steps":["validate"]}""", "unknownWorkItem")]
    [InlineData("""{"workItemId":"repository:example/shop","steps":["validate"],"pullRequests":[null]}""", "planChanged")]
    [InlineData("""{"workItemId":"repository:example/shop","steps":["validate"],"pullRequests":[{"number":7}]}""", "planChanged")]
    public async Task A_body_with_missing_or_odd_fields_is_refused_with_a_code_and_records_nothing(string body, string code)
    {
        using var factory = new ForgeApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/api/v1/approvals", new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        (await ErrorOf(response)).Should().Be(code);
        (await Json(await client.GetAsync("/api/v1/approvals", Ct))).EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_not_even_routed_to_the_approval()
    {
        using var factory = new ForgeApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/api/v1/approvals", new StringContent($"workItemId={Shop}&steps=validate", Encoding.UTF8, "application/x-www-form-urlencoded"), Ct);

        // The route only consumes JSON, so a cross-site form falls to the 404 of the fallback of the API: nothing is decided
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(await client.GetAsync("/api/v1/approvals", Ct))).EnumerateArray().Should().BeEmpty();
    }

    // --- the global stop

    [Fact]
    public async Task The_kill_switch_stops_approving_and_says_so_in_the_status()
    {
        using var factory = new ForgeApiFactory();
        var client = factory.CreateClient();

        (await Stop(client, true)).StatusCode.Should().Be(HttpStatusCode.OK);
        var refused = await Approve(client);
        var status = (await Json(await client.GetAsync("/api/v1/system/status", Ct))).GetProperty("approvals");

        refused.StatusCode.Should().Be(HttpStatusCode.Locked);
        (await ErrorOf(refused)).Should().Be("killSwitchEngaged");
        status.GetProperty("killSwitchEngaged").GetBoolean().Should().BeTrue();
        (await Json(await client.GetAsync("/api/v1/safety", Ct))).GetProperty("killSwitchEngaged").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Releasing_the_kill_switch_allows_approving_again_and_both_changes_are_traced()
    {
        using var factory = new ForgeApiFactory();
        var client = factory.CreateClient();
        await Stop(client, true);
        await Stop(client, false);

        var allowed = await Approve(client);

        allowed.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Json(await client.GetAsync("/api/v1/audit", Ct))).EnumerateArray().Select(e => e.GetProperty("event").GetString())
            .Should().Contain(["killswitch.engaged", "killswitch.released", "approval.recorded"]);
    }

    [Fact]
    public async Task The_stop_works_even_when_the_trail_cannot_be_written()
    {
        using var factory = new ForgeApiFactory { ServiceOverrides = FullTrail };
        var client = factory.CreateClient();

        var response = await Stop(client, true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(response)).GetProperty("killSwitchEngaged").GetBoolean().Should().BeTrue();
        (await Json(await client.GetAsync("/api/v1/audit", Ct))).EnumerateArray().Should().BeEmpty();
    }

    // --- the powers

    [Fact]
    public async Task The_plan_says_which_steps_are_granted()
    {
        using var factory = new ForgeApiFactory();

        var item = await Json(await factory.CreateClient().GetAsync($"/api/v1/work-items/{Shop}", Ct));

        item.GetProperty("plan").EnumerateArray().Where(s => s.GetProperty("environmentId").ValueKind == JsonValueKind.Null)
            .Should().OnlyContain(s => s.GetProperty("granted").GetBoolean());
    }

    [Theory]
    [InlineData("example/shop")]
    [InlineData("*")]
    public async Task A_grant_of_the_configuration_makes_exactly_its_steps_granted(string repository)
    {
        using var factory = new ForgeApiFactory
        {
            EnvironmentName = "Testing",
            Settings = new Dictionary<string, string?> { ["Powers:Grants:0:Repository"] = repository, ["Powers:Grants:0:Steps:0"] = "validate" },
        };

        var plan = (await Json(await factory.CreateClient().GetAsync($"/api/v1/work-items/{Shop}", Ct))).GetProperty("plan").EnumerateArray().ToList();

        plan.Single(s => s.GetProperty("kind").GetString() == "validate").GetProperty("granted").GetBoolean().Should().BeTrue();
        plan.Single(s => s.GetProperty("kind").GetString() == "mergePullRequests").GetProperty("granted").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("example/shop", "bogus")]
    [InlineData("not-a-repository", "validate")]
    [InlineData("", "validate")]
    [InlineData("*", "mergePullRequests")]
    public void A_grant_that_makes_no_sense_or_is_too_wide_stops_the_host_from_starting(string repository, string step)
    {
        using var factory = new ForgeApiFactory
        {
            EnvironmentName = "Testing",
            Settings = new Dictionary<string, string?> { ["Powers:Grants:0:Repository"] = repository, ["Powers:Grants:0:Steps:0"] = step },
        };

        var start = () => factory.CreateClient();

        start.Should().Throw<Exception>().Which.ToString().Should().Contain("Powers:Grants");
    }

    // --- who may call

    [Fact]
    public async Task The_trail_numbers_its_entries_from_the_oldest_and_the_numbers_do_not_move()
    {
        using var factory = new ForgeApiFactory();
        var client = factory.CreateClient();
        await Stop(client, true);
        await Stop(client, false);

        static int[] Ids(JsonElement trail) => [.. trail.EnumerateArray().Select(entry => entry.GetProperty("id").GetInt32())];
        var before = Ids(await Json(await client.GetAsync("/api/v1/audit", Ct)));
        await Stop(client, true);
        var after = Ids(await Json(await client.GetAsync("/api/v1/audit", Ct)));

        before.Should().Equal(2, 1);
        after.Should().Equal(3, 2, 1);
    }

    [Theory]
    [InlineData("Approvals:QueueCapacity", "0")]
    [InlineData("Approvals:QueueCapacity", "-1")]
    [InlineData("Approvals:AuditCapacity", "0")]
    [InlineData("Approvals:ApprovalsLimit", "0")]
    [InlineData("Approvals:ApprovalsWindowMinutes", "0")]
    public void A_capacity_of_zero_or_less_stops_the_host_from_starting(string key, string value)
    {
        using var factory = new ForgeApiFactory { Settings = new Dictionary<string, string?> { [key] = value } };

        var start = () => factory.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage("*greater than zero*");
    }

    [Fact]
    public void A_trail_of_one_place_stops_the_host_from_starting_because_that_place_is_the_stops()
    {
        using var factory = new ForgeApiFactory { Settings = new Dictionary<string, string?> { ["Approvals:AuditCapacity"] = "1" } };

        var start = () => factory.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage("*at least 2*");
    }

    [Fact]
    public async Task Approving_is_limited_per_person_and_a_person_held_back_does_not_hold_back_another()
    {
        using var factory = new ForgeApiFactory
        {
            Settings = new Dictionary<string, string?> { ["Approvals:ApprovalsLimit"] = "1", ["Approvals:ApprovalsWindowMinutes"] = "600" },
            ServiceOverrides = services => Replace<IApproverAccessor>(services, new HeaderApprover()),
        };
        var client = factory.CreateClient();

        async Task<HttpStatusCode> ApproveAs(string user)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/approvals")
            {
                Content = JsonContent.Create(new { workItemId = Shop, steps = new[] { "validate" }, pullRequests = new[] { new { number = 7, headSha = Head } } }),
            };
            request.Headers.Add("X-Test-User", user);
            return (await client.SendAsync(request, Ct)).StatusCode;
        }

        (await ApproveAs("alice")).Should().Be(HttpStatusCode.Created);
        (await ApproveAs("alice")).Should().Be(HttpStatusCode.TooManyRequests, "alice used her turn");
        (await ApproveAs("bob")).Should().Be(HttpStatusCode.Conflict, "bob is not held back: the decision is already there, and that is the answer he gets");
    }
}
