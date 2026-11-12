using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Host.Live;
using Orchestrarr.Host.Tasks;
using Orchestrarr.Core.Approvals;

namespace Orchestrarr.Host.Test;

/// <summary>When the host tells the screens that something changed, and when it must not (ADR 0025).</summary>
public sealed class LivePublishTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Recorder : ILiveNotifier
    {
        public ConcurrentQueue<(string Topic, string? Id)> Published { get; } = new();

        public void Publish(string topic, string? id = null) => Published.Enqueue((topic, id));

        public string[] Topics => [.. Published.Select(item => item.Topic).Distinct().Order()];
    }

    private static async Task<int> Pass(string method, string path, int status)
    {
        var recorder = new Recorder();
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        var middleware = new LiveChangeMiddleware(http =>
        {
            http.Response.StatusCode = status;
            return Task.CompletedTask;
        }, recorder);

        await middleware.InvokeAsync(context);

        return recorder.Published.Count;
    }

    [Theory]
    [InlineData("PUT", "/api/v1/safety/kill-switch", 200, 4)]
    [InlineData("POST", "/api/v1/approvals", 201, 4)]
    [InlineData("POST", "/api/v1/accounts/", 201, 5)]
    [InlineData("PUT", "/api/v1/options/x", 200, 4 + 1)]
    public async Task A_write_that_succeeded_announces_what_it_touches(string method, string path, int status, int expected)
    {
        (await Pass(method, path, status)).Should().Be(expected);
    }

    [Theory]
    [InlineData("GET", "/api/v1/runs", 200)]
    [InlineData("HEAD", "/api/v1/runs", 200)]
    [InlineData("OPTIONS", "/api/v1/runs", 204)]
    [InlineData("POST", "/api/v1/approvals", 409)]
    [InlineData("POST", "/api/v1/approvals", 403)]
    [InlineData("POST", "/api/v1/auth/login", 200)]
    [InlineData("POST", "/api/v1/auth/logout", 204)]
    [InlineData("POST", "/api/v1/runner/claim", 200)]
    [InlineData("POST", "/api/v1/live/negotiate", 200)]
    [InlineData("POST", "/health", 200)]
    public async Task A_read_a_refused_write_the_sign_in_the_runners_and_the_channel_itself_announce_nothing(string method, string path, int status)
    {
        (await Pass(method, path, status)).Should().Be(0);
    }

    [Fact]
    public async Task The_host_announces_a_change_made_through_the_api_and_nothing_for_a_read_or_a_refusal()
    {
        var recorder = new Recorder();
        using var factory = new ForgeApiFactory { ServiceOverrides = services => { services.RemoveAll<ILiveNotifier>(); services.AddSingleton<ILiveNotifier>(recorder); } };
        var client = factory.CreateClient();
        await client.GetAsync("/api/v1/safety", Ct);
        recorder.Published.Clear();

        await client.GetAsync("/api/v1/safety", Ct);
        await client.PostAsJsonAsync("/api/v1/approvals", new { workItemId = "repository:nope", steps = new[] { "validate" }, pullRequests = Array.Empty<object>() }, Ct);
        // Reading the fleet for the first time may tell the table (problems); a read and a refused write change nothing else
        recorder.Topics.Should().NotContain([LiveTopics.Approvals, LiveTopics.Audit, LiveTopics.Runs, LiveTopics.Status]);
        recorder.Published.Clear();
        var stopped = await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct);

        stopped.StatusCode.Should().Be(HttpStatusCode.OK);
        recorder.Topics.Should().Equal(LiveTopics.Approvals, LiveTopics.Audit, LiveTopics.Runs, LiveTopics.Status);
    }

    [Fact]
    public async Task The_table_is_still_served_from_the_forge_alone_when_the_results_of_the_workshop_cannot_be_read()
    {
        var queue = Substitute.For<IBatchQueue>();
        queue.TaskResults(Arg.Any<int>()).Returns(_ => throw new StorageUnavailableException("the file cannot be read", new IOException()));
        using var factory = new ForgeApiFactory { ServiceOverrides = services => { services.RemoveAll<IBatchQueue>(); services.AddSingleton(queue); } };

        var response = await factory.CreateClient().GetAsync("/api/v1/problems", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct)).GetProperty("rows").EnumerateArray().ToList();
        rows.Should().NotBeEmpty().And.OnlyContain(row => row.GetProperty("workshop").GetString() == "unknown", "with no result, the workshop has said nothing of any commit");
    }

    private static RunnerHello Hello(string version = "1", params string[] repositories) =>
        new("runner-1", version, [TaskKind.ValidatePullRequest], repositories.Length == 0 ? ["example/*"] : repositories, [], true);

    [Fact]
    public void The_registry_announces_a_runner_that_appears_comes_back_or_changes_and_not_every_call_it_makes()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
        var registry = new RunnerRegistry(time);
        var announced = 0;
        registry.Changed += () => announced++;

        registry.Seen("runner-1", Hello());
        announced.Should().Be(1, "it appeared");
        time.Advance(TimeSpan.FromSeconds(3));
        registry.Seen("runner-1", Hello());
        time.Advance(TimeSpan.FromSeconds(3));
        registry.Seen("runner-1", Hello());
        announced.Should().Be(1, "a runner asks every few seconds: the same description is not news");

        registry.Seen("runner-1", Hello(repositories: "example/other"));
        announced.Should().Be(2, "what it accepts changed");
        time.Advance(RunnerRegistry.ConnectedFor + TimeSpan.FromSeconds(1));
        registry.Seen("runner-1", Hello(repositories: "example/other"));
        announced.Should().Be(3, "it came back after a silence");
    }
}
