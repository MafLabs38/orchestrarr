using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Orchestrarr.Core.Forge;

namespace Orchestrarr.Host.Test;

public sealed class PullRequestsApiTests
{
    private static async Task<JsonElement> ItemAsync(HttpClient client, string id) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/v1/work-items/{id}", TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_pull_requests_behind_a_finding_are_part_of_the_work_item()
    {
        using var factory = new ForgeApiFactory();

        var item = await ItemAsync(factory.CreateClient(), "repository:example/shop");

        item.GetProperty("pullRequestsError").ValueKind.Should().Be(JsonValueKind.Null);
        var pullRequest = item.GetProperty("pullRequests").EnumerateArray().Should().ContainSingle().Which;
        pullRequest.GetProperty("number").GetInt32().Should().Be(7);
        pullRequest.GetProperty("url").GetString().Should().Be("https://forge.example/example/shop/pull/7");
        pullRequest.GetProperty("checks").GetString().Should().Be("passing");
        pullRequest.GetProperty("merge").GetString().Should().Be("clean");
        var change = pullRequest.GetProperty("changes").EnumerateArray().Single();
        change.GetProperty("package").GetString().Should().Be("lodash");
        change.GetProperty("kind").GetString().Should().Be("major");
    }

    [Fact]
    public async Task A_work_item_with_no_pull_request_waiting_does_not_ask_the_forge()
    {
        using var factory = new ForgeApiFactory();

        var item = await ItemAsync(factory.CreateClient(), "repository:example/api-gateway");

        item.GetProperty("pullRequests").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("pullRequestsError").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_forge_that_fails_gives_a_code_on_the_work_items_and_does_not_fail_the_report()
    {
        using var factory = new ForgeApiFactory { Fails = true };
        var client = factory.CreateClient();

        var report = await client.GetFromJsonAsync<JsonElement>("/api/v1/work-items", TestContext.Current.CancellationToken);
        var item = await ItemAsync(client, "repository:example/shop");
        var text = await client.GetStringAsync("/api/v1/work-items", TestContext.Current.CancellationToken);

        report.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("pullRequests").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("pullRequestsError").GetString().Should().Be("unauthorized");
        text.Should().NotContain("rate limited");
    }

    [Fact]
    public async Task Without_a_forge_the_pull_requests_are_not_read_and_the_status_says_so()
    {
        using var factory = new NoForgeApiFactory();
        var client = factory.CreateClient();

        var item = await ItemAsync(client, "repository:example/shop");
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/status", TestContext.Current.CancellationToken);

        item.GetProperty("pullRequests").ValueKind.Should().Be(JsonValueKind.Null);
        status.GetProperty("forge").GetProperty("configured").GetBoolean().Should().BeFalse();
        status.GetProperty("forge").GetProperty("kind").GetString().Should().Be("none");
    }

    [Fact]
    public async Task The_sample_forge_gives_the_pull_requests_of_the_sample_file()
    {
        using var factory = new SampleApiFactory();

        var item = await ItemAsync(factory.CreateClient(), "repository:example/billing-api");

        var pullRequests = item.GetProperty("pullRequests").EnumerateArray().ToList();
        pullRequests.Should().HaveCount(7);
        pullRequests.Select(p => p.GetProperty("checks").GetString()).Should().Contain(["failing", "passing"]);
        pullRequests.Select(p => p.GetProperty("merge").GetString()).Should().Contain("conflicting");
    }

    [Fact]
    public async Task With_a_forge_the_status_says_which_one()
    {
        using var factory = new ForgeApiFactory();

        var status = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/system/status", TestContext.Current.CancellationToken);

        status.GetProperty("forge").GetProperty("configured").GetBoolean().Should().BeTrue();
        status.GetProperty("forge").GetProperty("kind").GetString().Should().Be("github");
    }

    [Theory]
    [InlineData("ghp_secret", null, "github", true)]
    [InlineData("ghp_secret", "pulls.json", "github", true)]
    [InlineData(null, "pulls.json", "sample", true)]
    [InlineData(null, null, "none", false)]
    [InlineData("  ", "  ", "none", false)]
    public void The_forge_follows_the_configuration_and_a_token_wins_over_a_sample(string? token, string? sampleFile, string expectedKind, bool configured)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GitHub:Token"] = token, [PullRequestSourceFactory.SampleFileKey] = sampleFile })
            .Build();

        IPullRequestSource source = PullRequestSourceFactory.Create(configuration, "/srv", TimeProvider.System);

        source.Kind.Should().Be(expectedKind);
        source.Configured.Should().Be(configured);
    }
}
