using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Orchestrarr.Core.Machines;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

/// <summary>The lab machines (ADR 0047): a mission is claimed only with a lease on an Idle machine of its runner, the state is told to the screens, and a runner touches its own machines only.</summary>
public sealed class MachineApiTests
{
    private const string Header = "X-Orchestrarr-Request";
    private static readonly string Token = new('m', 48);
    private static readonly string Head = new('d', 40);
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private sealed class MachineFactory : AccountsApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Development");
            builder.UseSetting("Missions:Sandbox", "true");
            builder.UseSetting("Missions:StagingRunner", "runner-staging");
            builder.UseSetting("Tasks:Runners:0:Name", "runner-staging");
            builder.UseSetting("Tasks:Runners:0:Token", Token);
            builder.UseSetting("Missions:Machines:0:Id", "leipzig");
            builder.UseSetting("Missions:Machines:0:Role", "Staging");
            builder.UseSetting("Missions:Machines:0:Runner", "runner-staging");
            builder.UseSetting("Missions:Machines:1:Id", "bilbao");
            builder.UseSetting("Missions:Machines:1:Role", "Release");
            builder.UseSetting("Missions:Machines:1:Runner", "runner-staging");
            builder.UseSetting("Missions:Machines:2:Id", "elsewhere");
            builder.UseSetting("Missions:Machines:2:Role", "Staging");
            builder.UseSetting("Missions:Machines:2:Runner", "another-runner");
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

    private static readonly object Hello = new { hello = new { name = "runner-staging", version = "1", kinds = new[] { "validatePullRequest" }, repositories = new[] { "example/*" }, delegations = Array.Empty<object>() } };

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

    private static object Checks(bool passed) => new { checks = new[] { new { name = "isolation", passed, detail = passed ? "closed" : "the control plane answers on 5180" }, new { name = "registries", passed = true, detail = "open" } } };

    private static async Task Qualify(HttpClient runner, string machine, bool passed)
    {
        (await runner.PostAsync($"/api/v1/runner/machines/{machine}/qualifying", null, Ct)).EnsureSuccessStatusCode();
        (await runner.PostAsJsonAsync($"/api/v1/runner/machines/{machine}/qualification", Checks(passed), Ct)).EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> ClaimAsync(HttpClient runner, string? machine) =>
        (await (await runner.PostAsJsonAsync("/api/v1/runner/missions/claim" + (machine is null ? string.Empty : $"?machine={machine}"), Hello, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("mission");

    private static async Task<JsonElement> MachineOf(HttpClient person, string id)
    {
        var all = await person.GetFromJsonAsync<JsonElement>("/api/v1/machines", Ct);
        return all.GetProperty("machines").EnumerateArray().Single(machine => machine.GetProperty("id").GetString() == id);
    }

    [Fact]
    public async Task At_start_every_machine_is_being_qualified_and_a_mission_is_not_given_to_a_machine_that_is_not_idle()
    {
        using var factory = new MachineFactory();
        await StartProblemAsync(factory);
        using var runner = Runner(factory, Token);
        using var person = await SignedIn(factory);

        (await MachineOf(person, "leipzig")).GetProperty("state").GetString().Should().Be("Qualifying");
        (await ClaimAsync(runner, "leipzig")).ValueKind.Should().Be(JsonValueKind.Null, "a machine is Idle only after a qualification");

        await Qualify(runner, "leipzig", passed: true);

        (await MachineOf(person, "leipzig")).GetProperty("state").GetString().Should().Be("Idle");
        (await ClaimAsync(runner, "leipzig")).GetProperty("missionId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_claim_takes_the_machine_and_the_view_says_who_holds_it_and_giving_it_back_makes_it_finished()
    {
        using var factory = new MachineFactory();
        var missionId = await StartProblemAsync(factory);
        using var runner = Runner(factory, Token);
        using var person = await SignedIn(factory);
        await Qualify(runner, "leipzig", passed: true);

        (await ClaimAsync(runner, "leipzig")).GetProperty("missionId").GetString().Should().Be(missionId);

        var held = await MachineOf(person, "leipzig");
        held.GetProperty("state").GetString().Should().Be("InProcess");
        held.GetProperty("missionId").GetString().Should().Be(missionId);
        held.GetProperty("stats").GetProperty("lots").GetInt32().Should().Be(1);
        (await (await runner.PostAsJsonAsync("/api/v1/runner/machines/leipzig/heartbeat", new { missionId }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("ok").GetBoolean().Should().BeTrue();

        (await (await runner.PostAsJsonAsync("/api/v1/runner/machines/leipzig/release", new { missionId }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("ok").GetBoolean().Should().BeTrue();

        (await MachineOf(person, "leipzig")).GetProperty("state").GetString().Should().Be("Finished");
    }

    [Fact]
    public async Task A_claim_with_no_machine_or_with_the_machine_of_another_world_is_refused_and_a_machine_that_failed_its_qualification_is_down_with_why()
    {
        using var factory = new MachineFactory();
        await StartProblemAsync(factory);
        using var runner = Runner(factory, Token);
        using var person = await SignedIn(factory);
        await Qualify(runner, "leipzig", passed: true);

        (await ClaimAsync(runner, null)).ValueKind.Should().Be(JsonValueKind.Null, "a world that works on a machine is claimed with its machine");
        (await ClaimAsync(runner, "bilbao")).ValueKind.Should().Be(JsonValueKind.Null, "bilbao is a release machine, the staging world needs a staging one");

        await Qualify(runner, "bilbao", passed: false);
        var down = await MachineOf(person, "bilbao");
        down.GetProperty("state").GetString().Should().Be("Down");
        down.GetProperty("reason").GetString().Should().Contain("isolation").And.Contain("5180");
        down.GetProperty("qualificationPassed").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_runner_touches_the_machines_that_the_configuration_gives_it_and_no_other()
    {
        using var factory = new MachineFactory();
        using var runner = Runner(factory, Token);

        (await runner.GetAsync("/api/v1/runner/machines/elsewhere", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await runner.PostAsync("/api/v1/runner/machines/elsewhere/qualifying", null, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await runner.PostAsJsonAsync("/api/v1/runner/machines/elsewhere/qualification", Checks(true), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await runner.PostAsJsonAsync("/api/v1/runner/machines/elsewhere/release", new { missionId = "m" }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await runner.GetAsync("/api/v1/runner/machines/unknown", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.Services.GetRequiredService<MachinePool>().Get("elsewhere")!.State.Should().Be(MachineState.Qualifying);
        (await ClaimAsync(runner, "elsewhere")).ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_person_asks_for_a_new_qualification_of_a_machine_that_is_down_and_an_anonymous_caller_cannot()
    {
        using var factory = new MachineFactory();
        using var runner = Runner(factory, Token);
        using var person = await SignedIn(factory);
        await Qualify(runner, "leipzig", passed: false);

        (await MachineOf(person, "leipzig")).GetProperty("state").GetString().Should().Be("Down");
        (await person.PostAsync("/api/v1/machines/leipzig/requalify", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await MachineOf(person, "leipzig")).GetProperty("state").GetString().Should().Be("Qualifying");
        using var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add(Header, "test");
        (await anonymous.PostAsync("/api/v1/machines/leipzig/requalify", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/v1/machines", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Without_machines_in_the_configuration_the_list_is_empty_and_nothing_is_tied_to_a_machine()
    {
        using var factory = new AccountsApiFactory();
        using var person = await SignedIn(factory);

        var all = await person.GetFromJsonAsync<JsonElement>("/api/v1/machines", Ct);

        all.GetProperty("machines").GetArrayLength().Should().Be(0);
    }
}
