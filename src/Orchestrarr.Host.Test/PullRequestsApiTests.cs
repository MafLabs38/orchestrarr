using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orchestrarr.Core.Forge;

namespace Orchestrarr.Host.Test;

public sealed class PullRequestsApiTests
{
    private static async Task<JsonElement> ItemAsync(HttpClient client, string id) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/v1/work-items/{id}", TestContext.Current.CancellationToken);

    private static readonly string[] PullRequestProperties =
        ["author", "changes", "checks", "createdAt", "draft", "headBranch", "headSha", "labels", "merge", "number", "readyToMerge", "title", "url"];

    [Fact]
    public async Task The_pull_requests_behind_a_finding_are_read_and_part_of_the_work_item()
    {
        using var factory = new ForgeApiFactory();

        var item = await ItemAsync(factory.CreateClient(), "repository:example/shop");

        var read = item.GetProperty("pullRequests");
        read.GetProperty("status").GetString().Should().Be("read");
        read.GetProperty("truncated").GetBoolean().Should().BeFalse();
        read.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
        var pullRequest = read.GetProperty("items").EnumerateArray().Should().ContainSingle().Which;
        pullRequest.GetProperty("number").GetInt32().Should().Be(7);
        pullRequest.GetProperty("url").GetString().Should().Be("https://forge.example/example/shop/pull/7");
        pullRequest.GetProperty("headSha").GetString().Should().Be(new string('c', 40));
        pullRequest.GetProperty("checks").GetString().Should().Be("passing");
        pullRequest.GetProperty("merge").GetString().Should().Be("clean");
        pullRequest.GetProperty("readyToMerge").GetBoolean().Should().BeTrue();
        pullRequest.GetProperty("changes").EnumerateArray().Single().GetProperty("kind").GetString().Should().Be("major");
    }

    [Fact]
    public async Task The_contract_of_a_pull_request_is_exactly_what_the_interface_depends_on()
    {
        using var factory = new ForgeApiFactory();

        var item = await ItemAsync(factory.CreateClient(), "repository:example/shop");

        var pullRequest = item.GetProperty("pullRequests").GetProperty("items").EnumerateArray().Single();
        pullRequest.EnumerateObject().Select(p => p.Name).Order().Should().Equal(PullRequestProperties);
        pullRequest.GetProperty("draft").ValueKind.Should().Be(JsonValueKind.False);
        pullRequest.GetProperty("labels").EnumerateArray().Select(l => l.GetString()).Should().Equal("dependencies");
    }

    [Fact]
    public async Task A_work_item_that_waits_for_no_pull_request_has_nothing_to_read()
    {
        using var factory = new ForgeApiFactory();

        var item = await ItemAsync(factory.CreateClient(), "repository:example/api-gateway");

        item.GetProperty("pullRequests").GetProperty("status").GetString().Should().Be("notNeeded");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "unauthorized")]
    [InlineData(HttpStatusCode.TooManyRequests, "rateLimited")]
    [InlineData(HttpStatusCode.BadGateway, "unreachable")]
    public async Task A_forge_that_fails_gives_a_code_on_the_work_items_and_does_not_fail_the_report(HttpStatusCode status, string code)
    {
        using var factory = new ForgeApiFactory { Failure = () => new HttpRequestException("internal-forge-detail", null, status) };
        var client = factory.CreateClient();

        var report = await client.GetFromJsonAsync<JsonElement>("/api/v1/work-items", TestContext.Current.CancellationToken);
        var text = await client.GetStringAsync("/api/v1/work-items", TestContext.Current.CancellationToken);

        report.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
        var read = (await ItemAsync(client, "repository:example/shop")).GetProperty("pullRequests");
        read.GetProperty("status").GetString().Should().Be("failed");
        read.GetProperty("error").GetString().Should().Be(code);
        text.Should().NotContain("internal-forge-detail");
    }

    [Fact]
    public async Task Without_a_forge_the_pull_requests_are_not_connected_and_the_status_says_so()
    {
        using var factory = new NoForgeApiFactory();
        var client = factory.CreateClient();

        var item = await ItemAsync(client, "repository:example/shop");
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/status", TestContext.Current.CancellationToken);

        item.GetProperty("pullRequests").GetProperty("status").GetString().Should().Be("notConnected");
        status.GetProperty("forge").GetProperty("configured").GetBoolean().Should().BeFalse();
        status.GetProperty("forge").GetProperty("kind").GetString().Should().Be("none");
    }

    [Fact]
    public async Task With_a_forge_the_status_says_which_one()
    {
        using var factory = new ForgeApiFactory();

        var status = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/system/status", TestContext.Current.CancellationToken);

        status.GetProperty("forge").GetProperty("configured").GetBoolean().Should().BeTrue();
        status.GetProperty("forge").GetProperty("kind").GetString().Should().Be("github");
    }

    [Fact]
    public async Task The_sample_forge_gives_the_pull_requests_of_the_sample_file_with_their_states()
    {
        using var factory = new SampleApiFactory();

        var item = await ItemAsync(factory.CreateClient(), "repository:example/billing-api");

        var pullRequests = item.GetProperty("pullRequests").GetProperty("items").EnumerateArray().ToList();
        pullRequests.Should().NotBeEmpty();
        pullRequests.Select(p => p.GetProperty("checks").GetString()).Should().Contain(["failing", "passing"]);
        pullRequests.Select(p => p.GetProperty("merge").GetString()).Should().Contain("conflicting");
    }

    [Fact]
    public async Task The_status_never_reads_the_fleet_it_says_what_the_last_read_found_and_nothing_before_the_first()
    {
        using var factory = new CountingApiFactory();
        var client = factory.CreateClient();

        var before = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/status", TestContext.Current.CancellationToken);
        await client.GetFromJsonAsync<JsonElement>("/api/v1/system/status", TestContext.Current.CancellationToken);
        var readsByStatus = factory.Source.Reads;
        await client.GetFromJsonAsync<JsonElement>("/api/v1/work-items", TestContext.Current.CancellationToken);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/status", TestContext.Current.CancellationToken);

        readsByStatus.Should().Be(0);
        before.GetProperty("source").GetProperty("readAt").ValueKind.Should().Be(JsonValueKind.Null);
        factory.Source.Reads.Should().Be(1);
        after.GetProperty("source").GetProperty("readAt").ValueKind.Should().Be(JsonValueKind.String);
    }
}
