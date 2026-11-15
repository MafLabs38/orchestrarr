using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Test;

public sealed class MissionsApiTests
{
    private const string Header = "X-Orchestrarr-Request";
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static async Task<HttpClient> SignedIn(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Header, "test");
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { name = AccountsApiFactory.AdminName, password = AccountsApiFactory.AdminPassword }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<JsonElement> Demo(HttpClient client, string query = "")
    {
        var response = await client.PostAsync($"/api/v1/dev/missions/demo{query}", null, Ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    [Fact]
    public async Task The_missions_need_a_session_and_an_unknown_one_is_a_404()
    {
        using var factory = new AccountsApiFactory();
        using var anonymous = factory.CreateClient();

        (await anonymous.GetAsync("/api/v1/missions", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/v1/missions/anything", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var client = await SignedIn(factory);
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/missions", Ct)).GetArrayLength().Should().Be(0);
        (await client.GetAsync("/api/v1/missions/unknown", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_problem_with_a_staging_goes_through_the_three_worlds_with_a_mandate_for_each_and_ends_closed()
    {
        using var factory = new AccountsApiFactory();
        using var client = await SignedIn(factory);

        var missions = await Demo(client);

        missions.GetArrayLength().Should().Be(3);
        var worlds = missions.EnumerateArray().Select(mission => mission.GetProperty("world").GetString()).ToList();
        worlds.Should().Equal("Staging", "Git", "Release");
        foreach (var mission in missions.EnumerateArray())
        {
            mission.GetProperty("state").GetString().Should().Be("Accepted");
            mission.GetProperty("problemState").GetString().Should().Be("Closed");
            mission.GetProperty("mandate").GetProperty("runnerId").GetString().Should().StartWith("runner-");
            mission.GetProperty("mandate").GetProperty("revoked").GetBoolean().Should().BeTrue("the power never outlives the work");
        }
        var staging = missions.EnumerateArray().Single(mission => mission.GetProperty("world").GetString() == "Staging");
        staging.GetProperty("mandate").GetProperty("source").GetString().Should().Be("PlanValidation");
        staging.GetProperty("dossier").GetProperty("complex").GetBoolean().Should().BeTrue();
        missions.EnumerateArray().Where(mission => mission.GetProperty("world").GetString() != "Staging").Should().OnlyContain(mission => mission.GetProperty("mandate").GetProperty("source").GetString() == "Controller");
        staging.GetProperty("mandate").GetProperty("acts").EnumerateArray().Select(act => act.GetString()).Should().Contain("ReserveStaging");
    }

    [Fact]
    public async Task An_application_declared_as_having_no_staging_starts_in_the_git_world()
    {
        using var factory = new AccountsApiFactory();
        using var client = await SignedIn(factory);

        var missions = await Demo(client, "?staging=false");

        missions.EnumerateArray().Select(mission => mission.GetProperty("world").GetString()).Should().Equal("Git", "Release");
    }

    [Fact]
    public async Task A_pipeline_that_is_not_green_sends_the_release_back_instead_of_closing_the_problem()
    {
        using var factory = new AccountsApiFactory();
        using var client = await SignedIn(factory);

        var missions = await Demo(client, "?scenario=red");

        var release = missions.EnumerateArray().Single(mission => mission.GetProperty("world").GetString() == "Release");
        release.GetProperty("state").GetString().Should().Be("Running");
        release.GetProperty("sendBacks").GetInt32().Should().Be(1);
        release.GetProperty("problemState").GetString().Should().Be("InProgress");
    }

    [Fact]
    public async Task An_escalation_by_the_controller_ends_the_mission_revokes_its_mandate_and_the_problem_waits_for_the_maintainer()
    {
        using var factory = new AccountsApiFactory();
        using var client = await SignedIn(factory);

        var missions = await Demo(client, "?scenario=escalate");

        var git = missions.EnumerateArray().Single(mission => mission.GetProperty("world").GetString() == "Git");
        git.GetProperty("state").GetString().Should().Be("Ended");
        git.GetProperty("end").GetString().Should().Be("Refused");
        git.GetProperty("endReason").GetString().Should().Contain("escalated");
        git.GetProperty("mandate").GetProperty("revoked").GetBoolean().Should().BeTrue();
        git.GetProperty("problemState").GetString().Should().Be("Escalated");
    }

    [Fact]
    public async Task The_list_has_the_newest_first_one_mission_can_be_read_and_the_signature_is_never_shown()
    {
        using var factory = new AccountsApiFactory();
        using var client = await SignedIn(factory);
        await Demo(client);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/missions?limit=2", Ct);
        var id = list[0].GetProperty("id").GetString();
        var raw = await client.GetStringAsync($"/api/v1/missions/{id}", Ct);

        list.GetArrayLength().Should().Be(2);
        list[0].GetProperty("world").GetString().Should().Be("Release");
        raw.Should().NotContainEquivalentOf("signature", "the signature stays in the control plane");
        raw.Should().NotContainEquivalentOf("approval").And.NotContainEquivalentOf("certificate", "the certificate of the authority stays in the control plane too");
        using var document = JsonDocument.Parse(raw);
        document.RootElement.GetProperty("mandate").EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("id", "runnerId", "source", "acts", "issuedAt", "expiresAt", "revoked");
    }

    [Fact]
    public async Task The_controller_is_run_through_the_gateway_with_the_prompt_and_its_answer_is_read_as_a_decision()
    {
        var gateway = new FakeGateway("```json\n{\"decision\":\"end\",\"reasons\":[\"frame broken\"],\"remarks\":\"\"}\n```");
        using var factory = new AccountsApiFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAgentGateway>();
            services.AddSingleton<IAgentGateway>(gateway);
        }));
        using var client = await SignedIn(factory);

        var response = await client.PostAsync("/api/v1/dev/controller/decide?hostile=true", null, Ct);
        var answer = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        answer.GetProperty("decision").GetString().Should().Be("End");
        gateway.Last!.UserPrompt.Should().Contain("DiffTouchesForbiddenPath").And.Contain("<<<DATA");
        gateway.Last.SystemPrompt.Should().Contain("UNTRUSTED DATA");
    }

    private sealed class FakeGateway(string text) : IAgentGateway
    {
        public AgentRequest? Last { get; private set; }

        public Task<AgentReply> RunAsync(AgentRequest request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new AgentReply(true, text, null));
        }
    }
}
