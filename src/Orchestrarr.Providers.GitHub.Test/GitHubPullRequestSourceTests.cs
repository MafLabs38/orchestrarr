using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Forge;

namespace Orchestrarr.Providers.GitHub.Test;

public sealed class GitHubPullRequestSourceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero));
    private readonly RoutingHandler _handler = new();

    private GitHubPullRequestSource Source(Action<GitHubOptions>? configure = null)
    {
        var options = new GitHubOptions { Token = "ghp_secret" };
        configure?.Invoke(options);
        return GitHubPullRequestSource.Create(options, _time, _handler);
    }

    private static string Pull(int number, string title, string login = "dependabot[bot]", string sha = "abc", bool draft = false, string labels = "") =>
        $$"""{ "number": {{number}}, "title": "{{title}}", "user": { "login": "{{login}}" }, "head": { "ref": "bot/x-{{number}}", "sha": "{{sha}}{{number}}" }, "draft": {{(draft ? "true" : "false")}}, "labels": [{{labels}}], "created_at": "2026-11-01T09:38:16Z", "html_url": "https://evil.example/pwn" }""";

    private void Serve(int number, string detailState = "clean", string checkRuns = """{ "check_runs": [ { "status": "completed", "conclusion": "success" } ] }""")
    {
        _handler.On($"/repos/example/app/pulls/{number}", $$"""{ "number": {{number}}, "mergeable_state": "{{detailState}}" }""");
        _handler.On($"/repos/example/app/commits/abc{number}/check-runs", checkRuns);
    }

    [Fact]
    public async Task Only_the_pull_requests_of_a_dependency_bot_are_listed_with_what_they_update()
    {
        _handler.On("/repos/example/app/pulls", $"[{Pull(1, "chore(deps): Bump lodash from 4.17.4 to 4.17.21", labels: "{ \"name\": \"dependencies\" }")}, {Pull(2, "Add a feature", login: "someone")}]");
        Serve(1);

        var pullRequests = await Source().ListDependencyPullRequestsAsync("example/app", TestContext.Current.CancellationToken);

        var pullRequest = pullRequests.Should().ContainSingle().Which;
        pullRequest.Number.Should().Be(1);
        pullRequest.Author.Should().Be("dependabot[bot]");
        pullRequest.HeadBranch.Should().Be("bot/x-1");
        pullRequest.Labels.Should().Equal("dependencies");
        pullRequest.Changes.Should().ContainSingle().Which.Should().Be(new DependencyChange("lodash", "4.17.4", "4.17.21", UpdateKind.Patch));
        pullRequest.Checks.Should().Be(ChecksState.Passing);
        pullRequest.Merge.Should().Be(MergeState.Clean);
        pullRequest.CreatedAt.Should().Be(new DateTimeOffset(2026, 11, 1, 9, 38, 16, TimeSpan.Zero));
    }

    [Fact]
    public async Task The_link_is_built_from_the_configured_site_never_taken_from_the_answer()
    {
        _handler.On("/repos/example/app/pulls", $"[{Pull(5, "Bump a from 1.0.0 to 1.0.1")}]");
        Serve(5);

        var pullRequests = await Source(o => o.WebUrl = new Uri("https://git.example/")).ListDependencyPullRequestsAsync("example/app", TestContext.Current.CancellationToken);

        pullRequests.Single().Url.Should().Be(new Uri("https://git.example/example/app/pull/5"));
    }

    [Theory]
    [InlineData("clean", MergeState.Clean)]
    [InlineData("unstable", MergeState.Unstable)]
    [InlineData("behind", MergeState.Behind)]
    [InlineData("blocked", MergeState.Blocked)]
    [InlineData("dirty", MergeState.Conflicting)]
    [InlineData("draft", MergeState.Draft)]
    [InlineData("unknown", MergeState.Unknown)]
    [InlineData("something-new", MergeState.Unknown)]
    public async Task The_merge_state_comes_from_the_detail_of_the_pull_request(string state, MergeState expected)
    {
        _handler.On("/repos/example/app/pulls", $"[{Pull(1, "Bump a from 1.0.0 to 1.0.1")}]");
        Serve(1, state);

        var pullRequests = await Source().ListDependencyPullRequestsAsync("example/app", TestContext.Current.CancellationToken);

        pullRequests.Single().Merge.Should().Be(expected);
    }

    [Theory]
    [InlineData("""[ { "status": "completed", "conclusion": "success" }, { "status": "completed", "conclusion": "skipped" }, { "status": "completed", "conclusion": "neutral" } ]""", ChecksState.Passing)]
    [InlineData("""[ { "status": "completed", "conclusion": "success" }, { "status": "completed", "conclusion": "failure" } ]""", ChecksState.Failing)]
    [InlineData("""[ { "status": "completed", "conclusion": "cancelled" } ]""", ChecksState.Failing)]
    [InlineData("""[ { "status": "completed", "conclusion": "timed_out" } ]""", ChecksState.Failing)]
    [InlineData("""[ { "status": "in_progress" }, { "status": "completed", "conclusion": "success" } ]""", ChecksState.Pending)]
    [InlineData("""[ { "status": "queued" } ]""", ChecksState.Pending)]
    [InlineData("""[ { "status": "in_progress" }, { "status": "completed", "conclusion": "failure" } ]""", ChecksState.Failing)]
    public async Task The_checks_of_a_pull_request_are_failing_if_one_failed_pending_if_one_is_running_else_passing(string runs, ChecksState expected)
    {
        _handler.On("/repos/example/app/pulls", $"[{Pull(1, "Bump a from 1.0.0 to 1.0.1")}]");
        Serve(1, checkRuns: $$"""{ "check_runs": {{runs}} }""");

        var pullRequests = await Source().ListDependencyPullRequestsAsync("example/app", TestContext.Current.CancellationToken);

        pullRequests.Single().Checks.Should().Be(expected);
    }

    [Theory]
    [InlineData("""{ "state": "success", "total_count": 1 }""", ChecksState.Passing)]
    [InlineData("""{ "state": "failure", "total_count": 2 }""", ChecksState.Failing)]
    [InlineData("""{ "state": "error", "total_count": 1 }""", ChecksState.Failing)]
    [InlineData("""{ "state": "pending", "total_count": 1 }""", ChecksState.Pending)]
    [InlineData("""{ "state": "pending", "total_count": 0 }""", ChecksState.Unknown)]
    public async Task Without_check_runs_the_combined_commit_status_is_used(string status, ChecksState expected)
    {
        _handler.On("/repos/example/app/pulls", $"[{Pull(1, "Bump a from 1.0.0 to 1.0.1")}]");
        Serve(1, checkRuns: """{ "check_runs": [] }""");
        _handler.On("/repos/example/app/commits/abc1/status", status);

        var pullRequests = await Source().ListDependencyPullRequestsAsync("example/app", TestContext.Current.CancellationToken);

        pullRequests.Single().Checks.Should().Be(expected);
    }

    [Fact]
    public async Task A_pull_request_with_nothing_that_checks_it_has_unknown_checks()
    {
        _handler.On("/repos/example/app/pulls", $"[{Pull(1, "Bump a from 1.0.0 to 1.0.1")}]");
        Serve(1, checkRuns: """{ "check_runs": [] }""");
        _handler.On("/repos/example/app/commits/abc1/status", """{ "state": "pending", "total_count": 0 }""");

        (await Source().ListDependencyPullRequestsAsync("example/app", TestContext.Current.CancellationToken)).Single().Checks.Should().Be(ChecksState.Unknown);
    }

    [Fact]
    public async Task At_most_the_configured_number_of_pull_requests_is_read_oldest_first()
    {
        _handler.On("/repos/example/app/pulls", $"[{Pull(3, "Bump c from 1.0.0 to 1.0.1")}, {Pull(1, "Bump a from 1.0.0 to 1.0.1")}, {Pull(2, "Bump b from 1.0.0 to 1.0.1")}]");
        Serve(1);
        Serve(2);
        Serve(3);

        var pullRequests = await Source(o => o.MaxPullRequests = 2).ListDependencyPullRequestsAsync("example/app", TestContext.Current.CancellationToken);

        pullRequests.Select(p => p.Number).Should().Equal(1, 2);
    }

    [Fact]
    public async Task What_was_read_for_a_repository_is_kept_for_the_cache_duration_then_read_again()
    {
        _handler.On("/repos/example/app/pulls", $"[{Pull(1, "Bump a from 1.0.0 to 1.0.1")}]");
        Serve(1);
        var source = Source(o => o.CacheSeconds = 120);

        await source.ListDependencyPullRequestsAsync("example/app", TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(119));
        await source.ListDependencyPullRequestsAsync("example/app", TestContext.Current.CancellationToken);
        var listCalls = _handler.Calls("/repos/example/app/pulls");
        _time.Advance(TimeSpan.FromSeconds(1));
        await source.ListDependencyPullRequestsAsync("example/app", TestContext.Current.CancellationToken);

        listCalls.Should().Be(1);
        _handler.Calls("/repos/example/app/pulls").Should().Be(2);
    }

    [Fact]
    public async Task Each_repository_has_its_own_cache()
    {
        _handler.On("/repos/example/app/pulls", "[]");
        _handler.On("/repos/example/other/pulls", "[]");
        var source = Source();

        await source.ListDependencyPullRequestsAsync("example/app", TestContext.Current.CancellationToken);
        await source.ListDependencyPullRequestsAsync("example/other", TestContext.Current.CancellationToken);

        _handler.Calls("/repos/example/app/pulls").Should().Be(1);
        _handler.Calls("/repos/example/other/pulls").Should().Be(1);
    }

    [Fact]
    public async Task The_token_is_sent_as_a_bearer_and_every_call_is_a_get()
    {
        _handler.On("/repos/example/app/pulls", $"[{Pull(1, "Bump a from 1.0.0 to 1.0.1")}]");
        Serve(1);

        await Source().ListDependencyPullRequestsAsync("example/app", TestContext.Current.CancellationToken);

        _handler.Requests.Should().NotBeEmpty().And.OnlyContain(r => r.Method == HttpMethod.Get);
        _handler.Requests.Should().OnlyContain(r => r.Headers.Authorization!.Scheme == "Bearer" && r.Headers.Authorization.Parameter == "ghp_secret");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_refusal_or_a_failure_is_an_error_that_keeps_the_status_and_is_not_cached(HttpStatusCode status)
    {
        _handler.On("/repos/example/app/pulls", "{}", status);
        var source = Source();

        var first = () => source.ListDependencyPullRequestsAsync("example/app", TestContext.Current.CancellationToken);
        var second = () => source.ListDependencyPullRequestsAsync("example/app", TestContext.Current.CancellationToken);

        (await first.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(status);
        await second.Should().ThrowAsync<HttpRequestException>();
        _handler.Calls("/repos/example/app/pulls").Should().Be(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("example")]
    [InlineData("example/app/extra")]
    [InlineData("../etc/passwd")]
    [InlineData("example/../app")]
    [InlineData("example/app?x=1")]
    [InlineData("example/app#frag")]
    [InlineData("exa mple/app")]
    [InlineData("example//app")]
    public async Task A_name_that_is_not_a_repository_never_reaches_a_url(string name)
    {
        var read = () => Source().ListDependencyPullRequestsAsync(name, TestContext.Current.CancellationToken);

        await read.Should().ThrowAsync<ArgumentException>();
        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void A_source_cannot_be_built_without_a_token()
    {
        var build = () => GitHubPullRequestSource.Create(new GitHubOptions(), _time);

        build.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task A_redirect_is_not_followed_so_the_token_never_goes_to_another_address()
    {
        var elsewhere = new Listener(null);
        var configured = new Listener(elsewhere.Address);
        using (elsewhere)
        using (configured)
        {
            var source = GitHubPullRequestSource.Create(new GitHubOptions { Token = "ghp_secret", ApiUrl = configured.Address }, _time);

            var read = () => source.ListDependencyPullRequestsAsync("example/app", TestContext.Current.CancellationToken);

            (await read.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.Found);
            elsewhere.Requests.Should().Be(0);
        }
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Body, HttpStatusCode Status)> _routes = [];
        private readonly List<HttpRequestMessage> _requests = [];

        public IReadOnlyList<HttpRequestMessage> Requests => _requests;

        public void On(string path, string body, HttpStatusCode status = HttpStatusCode.OK) => _routes[path] = (body, status);

        public int Calls(string path) => _requests.Count(r => r.RequestUri!.AbsolutePath == path);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Add(request);
            var (body, status) = _routes.GetValueOrDefault(request.RequestUri!.AbsolutePath, ("{}", HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    // A real local server: the redirect behavior belongs to the network handler, which a stub would bypass
    private sealed class Listener : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private int _requests;

        public Listener(Uri? redirectTo)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Address = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(Address.ToString());
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        var context = await _listener.GetContextAsync();
                        Interlocked.Increment(ref _requests);
                        context.Response.StatusCode = redirectTo is null ? 200 : 302;
                        if (redirectTo is not null)
                        {
                            context.Response.RedirectLocation = redirectTo.ToString();
                        }

                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("[]"));
                        context.Response.Close();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }
                }
            });
        }

        public Uri Address { get; }

        public int Requests => Volatile.Read(ref _requests);

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            _stop.Dispose();
        }
    }
}
