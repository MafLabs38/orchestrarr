using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Orchestrarr.Host.Test;

/// <summary>The sample fleet in the Testing environment: no sandbox operator and no grant, which is what a production host starts as.</summary>
public sealed class ApprovalsDisabledApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting(FleetSourceFactory.SampleFileKey, Path.Combine(AppContext.BaseDirectory, "portarr-maintenance.sample.json"));
    }
}

/// <summary>The sandbox operator asked for outside Development: the host must refuse to start.</summary>
public sealed class SandboxOperatorInProductionFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("Approvals:Mode", "Development");
    }
}

public sealed class ApprovalsApiTests
{
    private const string Shop = "repository:example/shop";
    private static readonly string Head = new('c', 40);
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> Approve(HttpClient client, string workItemId = Shop, params string[] steps) =>
        client.PostAsJsonAsync("/api/v1/approvals", new { workItemId, steps = steps.Length == 0 ? ["validate", "mergePullRequests"] : steps }, Ct);

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task The_status_says_who_may_approve_and_whether_the_stop_is_engaged()
    {
        using var factory = new ForgeApiFactory();

        var approvals = (await Json(await factory.CreateClient().GetAsync("/api/v1/system/status", Ct))).GetProperty("approvals");

        approvals.GetProperty("mode").GetString().Should().Be("development");
        approvals.GetProperty("enabled").GetBoolean().Should().BeTrue();
        approvals.GetProperty("operator").GetString().Should().Be("dev-operator");
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
        approval.GetProperty("approver").GetString().Should().Be("dev-operator");
        approval.GetProperty("steps").EnumerateArray().Select(s => s.GetString()).Should().Equal("validate", "mergePullRequests");

        (await Json(await client.GetAsync("/api/v1/approvals", Ct))).EnumerateArray().Should().ContainSingle();
        var activity = (await Json(await client.GetAsync("/api/v1/activity", Ct))).EnumerateArray().Should().ContainSingle().Which;
        activity.GetProperty("event").GetString().Should().Be("approval.recorded");
        activity.GetProperty("subject").GetString().Should().Be("dev-operator");
    }

    [Fact]
    public async Task The_client_cannot_choose_the_commit_the_repository_or_the_approver()
    {
        using var factory = new ForgeApiFactory();
        var client = factory.CreateClient();
        var body = new StringContent(
            $$"""{"workItemId":"{{Shop}}","steps":["validate"],"headSha":"{{new string('f', 40)}}","repository":"evil/repo","approver":"mallory","pullRequest":1}""",
            Encoding.UTF8, "application/json");

        var approval = (await Json(await client.PostAsync("/api/v1/approvals", body, Ct))).GetProperty("approvals")[0];

        approval.GetProperty("headSha").GetString().Should().Be(Head);
        approval.GetProperty("repository").GetString().Should().Be("example/shop");
        approval.GetProperty("approver").GetString().Should().Be("dev-operator");
    }

    [Fact]
    public async Task The_same_approval_twice_is_a_conflict()
    {
        using var factory = new ForgeApiFactory();
        var client = factory.CreateClient();
        await Approve(client);

        var again = await Approve(client);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(again)).GetProperty("error").GetString().Should().Be("alreadyApproved");
    }

    [Theory]
    [InlineData("repository:example/nothing", "validate", HttpStatusCode.NotFound, "unknownWorkItem")]
    [InlineData(Shop, "mergePullRequests", HttpStatusCode.UnprocessableEntity, "mergeNeedsValidation")]
    [InlineData(Shop, "deploy", HttpStatusCode.UnprocessableEntity, "stepNotOffered")]
    public async Task A_refusal_is_a_status_and_a_code_never_a_sentence(string id, string step, HttpStatusCode status, string code)
    {
        using var factory = new ForgeApiFactory();

        var response = await Approve(factory.CreateClient(), id, step);

        var body = await Json(response);
        response.StatusCode.Should().Be(status);
        body.EnumerateObject().Select(p => p.Name).Should().Equal("error");
        body.GetProperty("error").GetString().Should().Be(code);
    }

    [Fact]
    public async Task Nothing_ticked_is_refused()
    {
        using var factory = new ForgeApiFactory();

        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/approvals", new { workItemId = Shop, steps = Array.Empty<string>() }, Ct);

        (await Json(response)).GetProperty("error").GetString().Should().Be("nothingTicked");
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_refused_before_anything_is_decided()
    {
        using var factory = new ForgeApiFactory();

        var client = factory.CreateClient();
        var response = await client.PostAsync("/api/v1/approvals", new StringContent($"workItemId={Shop}&steps=validate", Encoding.UTF8, "application/x-www-form-urlencoded"), Ct);

        // Refused before anything is decided: a 415, or the 404 that the fallback of the API gives when the content type does not match.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.UnsupportedMediaType, HttpStatusCode.NotFound);
        (await Json(await client.GetAsync("/api/v1/approvals", Ct))).EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task The_kill_switch_stops_approving_is_traced_and_can_be_released()
    {
        using var factory = new ForgeApiFactory();
        var client = factory.CreateClient();

        (await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var refused = await Approve(client);
        var status = (await Json(await client.GetAsync("/api/v1/system/status", Ct))).GetProperty("approvals");
        await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = false }, Ct);
        var allowed = await Approve(client);

        refused.StatusCode.Should().Be(HttpStatusCode.Locked);
        (await Json(refused)).GetProperty("error").GetString().Should().Be("killSwitchEngaged");
        status.GetProperty("killSwitchEngaged").GetBoolean().Should().BeTrue();
        allowed.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Json(await client.GetAsync("/api/v1/activity", Ct))).EnumerateArray().Select(e => e.GetProperty("event").GetString())
            .Should().Contain(["killswitch.engaged", "killswitch.released", "approval.refused", "approval.recorded"]);
    }

    [Fact]
    public async Task The_plan_says_which_steps_are_granted()
    {
        using var factory = new ForgeApiFactory();

        var item = await Json(await factory.CreateClient().GetAsync($"/api/v1/work-items/{Shop}", Ct));

        item.GetProperty("plan").EnumerateArray().Where(s => s.GetProperty("environmentId").ValueKind == JsonValueKind.Null)
            .Should().OnlyContain(s => s.GetProperty("granted").GetBoolean());
    }

    [Fact]
    public async Task Without_the_sandbox_operator_nobody_can_approve_or_stop()
    {
        using var factory = new ApprovalsDisabledApiFactory();
        var client = factory.CreateClient();

        var approve = await Approve(client);
        var stop = await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct);
        var status = (await Json(await client.GetAsync("/api/v1/system/status", Ct))).GetProperty("approvals");
        var item = await Json(await client.GetAsync($"/api/v1/work-items/{Shop}", Ct));

        approve.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(approve)).GetProperty("error").GetString().Should().Be("approvalsDisabled");
        stop.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        status.GetProperty("enabled").GetBoolean().Should().BeFalse();
        status.GetProperty("operator").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("plan").EnumerateArray().Should().OnlyContain(s => !s.GetProperty("granted").GetBoolean());
    }

    [Fact]
    public void The_sandbox_operator_is_refused_outside_the_development_environment()
    {
        using var factory = new SandboxOperatorInProductionFactory();

        var start = () => factory.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage("*only allowed in the Development environment*");
    }

    [Fact]
    public async Task Every_route_that_changes_something_is_one_of_the_two_that_the_operator_uses()
    {
        using var factory = new ForgeApiFactory();
        var client = factory.CreateClient();

        foreach (var path in new[] { "/api/v1/work-items", "/api/v1/activity", "/api/v1/approvals" })
        {
            (await client.DeleteAsync(path, Ct)).StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        }
        (await client.PostAsJsonAsync("/api/v1/work-items", new { }, Ct)).StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }
}
