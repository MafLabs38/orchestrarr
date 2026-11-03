using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Forge;

namespace Orchestrarr.Providers.GitHub.Test;

public sealed class GitHubPullRequestSourceTests
{
    private static readonly string Sha1 = new('a', 40);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero));
    private readonly RoutingHandler _handler = new();

    private GitHubPullRequestSource Source(Action<GitHubOptions>? configure = null)
    {
        var options = new GitHubOptions { Token = "ghp_secret" };
        configure?.Invoke(options);
        return GitHubPullRequestSource.Create(options, _time, _handler);
    }

    private static Task<PullRequestList> ListAsync(GitHubPullRequestSource source, string repository = "example/app", bool refresh = false) =>
        source.ListDependencyPullRequestsAsync(repository, refresh, TestContext.Current.CancellationToken);

    private static string Pull(int number, string title = "Bump a from 1.0.0 to 1.0.1", string? login = "dependabot[bot]", bool draft = false, string labels = "", string? sha = null)
    {
        var user = login is null ? "null" : $$"""{ "login": "{{login}}" }""";
        return $$"""{ "number": {{number}}, "title": "{{title}}", "user": {{user}}, "head": { "ref": "bot/x-{{number}}", "sha": "{{sha ?? Sha1}}" }, "draft": {{(draft ? "true" : "false")}}, "labels": [{{labels}}], "created_at": "2026-11-01T09:38:16Z", "html_url": "https://evil.example/pwn" }""";
    }

    private void Serve(int number, string detailState = "clean", string checkRuns = """{ "check_runs": [ { "status": "completed", "conclusion": "success" } ] }""", string repository = "example/app")
    {
        _handler.On($"/repos/{repository}/pulls/{number}", $$"""{ "number": {{number}}, "mergeable_state": "{{detailState}}" }""");
        _handler.On($"/repos/{repository}/commits/{Sha1}/check-runs", checkRuns);
    }

    private void List(string json, string repository = "example/app") => _handler.On($"/repos/{repository}/pulls", json);

    [Fact]
    public async Task The_pull_requests_of_a_bot_are_listed_with_what_they_update_and_everything_the_screens_need()
    {
        List($"[{Pull(1, "chore(deps): Bump lodash from 4.17.4 to 4.17.21", labels: "{ \"name\": \"dependencies\" }")}, {Pull(2, draft: true)}]");
        Serve(1);
        Serve(2, "draft");

        var list = await ListAsync(Source());

        list.Truncated.Should().BeFalse();
        var first = list.Items[0];
        first.Number.Should().Be(1);
        first.Author.Should().Be("dependabot[bot]");
        first.HeadBranch.Should().Be("bot/x-1");
        first.HeadSha.Should().Be(Sha1);
        first.Draft.Should().BeFalse();
        first.Labels.Should().Equal("dependencies");
        first.Changes.Should().ContainSingle().Which.Should().Be(new DependencyChange("lodash", "4.17.4", "4.17.21", UpdateKind.Patch));
        first.Checks.Should().Be(ChecksState.Passing);
        first.Merge.Should().Be(MergeState.Clean);
        first.ReadyToMerge.Should().BeTrue();
        first.CreatedAt.Should().Be(new DateTimeOffset(2026, 11, 1, 9, 38, 16, TimeSpan.Zero));
        list.Items[1].Draft.Should().BeTrue();
        list.Items[1].ReadyToMerge.Should().BeFalse();
    }

    [Theory]
    [InlineData("dependabot[bot]", true)]
    [InlineData("renovate[bot]", true)]
    [InlineData("Dependabot[BOT]", true)]
    [InlineData("someone", false)]
    [InlineData(null, false)]
    public async Task Only_the_default_bots_are_read_whatever_their_case(string? login, bool read)
    {
        List($"[{Pull(1, login: login)}]");
        Serve(1);

        (await ListAsync(Source())).Items.Should().HaveCount(read ? 1 : 0);
    }

    [Fact]
    public async Task A_list_of_bots_in_the_configuration_replaces_the_default_it_is_not_added_to_it()
    {
        List($"[{Pull(1, login: "dependabot[bot]")}, {Pull(2, login: "my-bot")}]");
        Serve(1);
        Serve(2);

        var list = await ListAsync(Source(o => o.BotLogins = ["my-bot"]));

        list.Items.Select(p => p.Number).Should().Equal(2);
    }

    [Fact]
    public async Task The_link_is_built_from_the_configured_site_never_taken_from_the_answer()
    {
        List($"[{Pull(5)}]");
        Serve(5);

        var list = await ListAsync(Source(o => o.WebUrl = new Uri("https://git.example/")));

        list.Items.Single().Url.Should().Be(new Uri("https://git.example/example/app/pull/5"));
    }

    [Theory]
    [InlineData("clean", MergeState.Clean)]
    [InlineData("unstable", MergeState.Unstable)]
    [InlineData("behind", MergeState.Behind)]
    [InlineData("blocked", MergeState.Blocked)]
    [InlineData("dirty", MergeState.Conflicting)]
    [InlineData("draft", MergeState.Draft)]
    [InlineData("something-new", MergeState.Unknown)]
    public async Task The_merge_state_comes_from_the_detail_of_the_pull_request(string state, MergeState expected)
    {
        List($"[{Pull(1)}]");
        Serve(1, state);

        (await ListAsync(Source())).Items.Single().Merge.Should().Be(expected);
    }

    [Theory]
    [InlineData("""[ { "status": "completed", "conclusion": "success" }, { "status": "completed", "conclusion": "skipped" }, { "status": "completed", "conclusion": "neutral" } ]""", ChecksState.Passing)]
    [InlineData("""[ { "status": "completed", "conclusion": "failure" } ]""", ChecksState.Failing)]
    [InlineData("""[ { "status": "completed", "conclusion": "cancelled" } ]""", ChecksState.Failing)]
    [InlineData("""[ { "status": "completed", "conclusion": "timed_out" } ]""", ChecksState.Failing)]
    [InlineData("""[ { "status": "completed", "conclusion": "action_required" } ]""", ChecksState.Failing)]
    [InlineData("""[ { "status": "completed", "conclusion": "startup_failure" } ]""", ChecksState.Failing)]
    [InlineData("""[ { "status": "in_progress" }, { "status": "completed", "conclusion": "success" } ]""", ChecksState.Pending)]
    [InlineData("""[ { "status": "queued" } ]""", ChecksState.Pending)]
    [InlineData("""[ { "status": "in_progress" }, { "status": "completed", "conclusion": "failure" } ]""", ChecksState.Failing)]
    public async Task The_checks_are_failing_if_one_failed_pending_if_one_is_running_else_passing(string runs, ChecksState expected)
    {
        List($"[{Pull(1)}]");
        Serve(1, checkRuns: $$"""{ "check_runs": {{runs}} }""");

        (await ListAsync(Source())).Items.Single().Checks.Should().Be(expected);
    }

    [Theory]
    [InlineData("""{ "state": "success", "total_count": 1 }""", ChecksState.Passing)]
    [InlineData("""{ "state": "failure", "total_count": 2 }""", ChecksState.Failing)]
    [InlineData("""{ "state": "error", "total_count": 1 }""", ChecksState.Failing)]
    [InlineData("""{ "state": "pending", "total_count": 1 }""", ChecksState.Pending)]
    [InlineData("""{ "state": "pending", "total_count": 0 }""", ChecksState.Unknown)]
    public async Task Without_check_runs_the_combined_commit_status_is_used(string status, ChecksState expected)
    {
        List($"[{Pull(1)}]");
        Serve(1, checkRuns: """{ "check_runs": [] }""");
        _handler.On($"/repos/example/app/commits/{Sha1}/status", status);

        (await ListAsync(Source())).Items.Single().Checks.Should().Be(expected);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_token_that_cannot_read_commit_statuses_gives_unknown_checks_not_a_failure_of_the_repository(HttpStatusCode status)
    {
        List($"[{Pull(1)}]");
        Serve(1, checkRuns: """{ "check_runs": [] }""");
        _handler.On($"/repos/example/app/commits/{Sha1}/status", "{}", status);

        (await ListAsync(Source())).Items.Single().Checks.Should().Be(ChecksState.Unknown);
    }

    [Fact]
    public async Task At_most_the_configured_number_is_read_oldest_first_and_the_rest_is_said()
    {
        List($"[{Pull(3)}, {Pull(1)}, {Pull(2)}]");
        Serve(1);
        Serve(2);
        Serve(3);

        var list = await ListAsync(Source(o => o.MaxPullRequests = 2));

        list.Items.Select(p => p.Number).Should().Equal(1, 2);
        list.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task The_next_page_is_followed_and_the_pull_requests_of_every_page_are_read()
    {
        _handler.On("/repos/example/app/pulls", $"[{Pull(1)}]", link: "<https://api.github.com/repos/example/app/pulls?state=open&per_page=100&page=2>; rel=\"next\", <https://api.github.com/x>; rel=\"last\"");
        _handler.On("/repos/example/app/pulls?state=open&per_page=100&page=2", $"[{Pull(2)}]");
        Serve(1);
        Serve(2);

        var list = await ListAsync(Source());

        list.Items.Select(p => p.Number).Should().Equal(1, 2);
        list.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Stopping_at_the_maximum_number_of_pages_is_said()
    {
        _handler.On("/repos/example/app/pulls", $"[{Pull(1)}]", link: "<https://api.github.com/repos/example/app/pulls?page=2>; rel=\"next\"");
        Serve(1);

        var list = await ListAsync(Source(o => o.MaxPages = 1));

        list.Items.Should().ContainSingle();
        list.Truncated.Should().BeTrue();
        _handler.Calls("/repos/example/app/pulls").Should().Be(1);
    }

    [Theory]
    [InlineData("<https://evil.example/repos/example/app/pulls?page=2>; rel=\"next\"")]
    [InlineData("<http://api.github.com/repos/example/app/pulls?page=2>; rel=\"next\"")]
    [InlineData("<https://api.github.com:8443/repos/example/app/pulls?page=2>; rel=\"next\"")]
    [InlineData("<not a url>; rel=\"next\"")]
    public async Task A_next_page_that_is_not_on_the_configured_api_is_not_followed_so_the_token_goes_nowhere_else(string link)
    {
        _handler.On("/repos/example/app/pulls", $"[{Pull(1)}]", link: link);
        Serve(1);

        var list = await ListAsync(Source());

        list.Items.Should().ContainSingle();
        _handler.Requests.Should().OnlyContain(r => r.RequestUri!.Host == "api.github.com" && r.RequestUri.Port == 443);
    }

    [Fact]
    public async Task What_was_read_for_a_repository_is_kept_for_the_cache_duration_then_read_again()
    {
        List($"[{Pull(1)}]");
        Serve(1);
        var source = Source(o => o.CacheSeconds = 120);

        await ListAsync(source);
        _time.Advance(TimeSpan.FromSeconds(119));
        await ListAsync(source);
        var callsBefore = _handler.Calls("/repos/example/app/pulls");
        _time.Advance(TimeSpan.FromSeconds(1));
        await ListAsync(source);

        callsBefore.Should().Be(1);
        _handler.Calls("/repos/example/app/pulls").Should().Be(2);
    }

    [Fact]
    public async Task A_refresh_reads_again_whatever_the_cache_says_and_each_repository_has_its_own()
    {
        List("[]");
        List("[]", "example/other");
        var source = Source();

        await ListAsync(source);
        await ListAsync(source, "example/other");
        await ListAsync(source, refresh: true);

        _handler.Calls("/repos/example/app/pulls").Should().Be(2);
        _handler.Calls("/repos/example/other/pulls").Should().Be(1);
    }

    [Fact]
    public async Task The_token_is_sent_as_a_bearer_and_every_call_is_a_get()
    {
        List($"[{Pull(1)}]");
        Serve(1);

        await ListAsync(Source());

        _handler.Requests.Should().NotBeEmpty().And.OnlyContain(r => r.Method == HttpMethod.Get);
        _handler.Requests.Should().OnlyContain(r => r.Headers.Authorization!.Scheme == "Bearer" && r.Headers.Authorization.Parameter == "ghp_secret");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_refusal_or_a_failure_of_the_list_is_an_error_that_keeps_the_status_and_is_not_kept(HttpStatusCode status)
    {
        _handler.On("/repos/example/app/pulls", "{}", status);
        var source = Source();

        var first = () => ListAsync(source);

        (await first.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(status);
        await first.Should().ThrowAsync<HttpRequestException>();
        _handler.Calls("/repos/example/app/pulls").Should().Be(2);
    }

    [Fact]
    public async Task A_429_with_a_delay_and_a_403_with_no_request_left_are_rate_limits_not_refusals()
    {
        _handler.On("/repos/example/app/pulls", "{}", HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(60));
        _handler.On("/repos/example/other/pulls", "{}", HttpStatusCode.Forbidden, remaining: "0");

        var first = () => ListAsync(Source());
        var second = () => ListAsync(Source(), "example/other");

        (await first.Should().ThrowAsync<ForgeRateLimitedException>()).Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(60));
        await second.Should().ThrowAsync<ForgeRateLimitedException>();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_pull_request_whose_detail_cannot_be_read_is_kept_with_what_is_unknown_and_the_others_are_read(HttpStatusCode status)
    {
        List($"[{Pull(1)}, {Pull(2)}]");
        _handler.On("/repos/example/app/pulls/1", "{}", status);
        Serve(2);

        var list = await ListAsync(Source());

        list.Items.Select(p => p.Number).Should().Equal(1, 2);
        list.Items[0].Merge.Should().Be(MergeState.Unknown);
        list.Items[0].Checks.Should().Be(ChecksState.Unknown);
        list.Items[1].ReadyToMerge.Should().BeTrue();
    }

    [Fact]
    public async Task A_pull_request_whose_checks_cannot_be_read_is_kept_with_unknown_checks()
    {
        List($"[{Pull(1)}]");
        Serve(1);
        _handler.On($"/repos/example/app/commits/{Sha1}/check-runs", "{}", HttpStatusCode.InternalServerError);

        (await ListAsync(Source())).Items.Single().Checks.Should().Be(ChecksState.Unknown);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_refusal_on_a_pull_request_is_about_everything_and_goes_up(HttpStatusCode status)
    {
        List($"[{Pull(1)}]");
        _handler.On("/repos/example/app/pulls/1", "{}", status);

        var read = () => ListAsync(Source());

        (await read.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(status);
    }

    [Fact]
    public async Task A_rate_limit_on_a_pull_request_is_about_everything_and_goes_up()
    {
        List($"[{Pull(1)}]");
        _handler.On("/repos/example/app/pulls/1", "{}", HttpStatusCode.TooManyRequests);

        var read = () => ListAsync(Source());

        await read.Should().ThrowAsync<ForgeRateLimitedException>();
    }

    [Fact]
    public async Task A_malformed_answer_is_an_error()
    {
        List("{bad");

        var read = () => ListAsync(Source());

        await read.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task An_answer_larger_than_the_limit_is_refused_instead_of_buffered()
    {
        List(new string(' ', 5_000) + "[]");

        var read = () => ListAsync(Source(o => o.MaxResponseBytes = 1_000));

        await read.Should().ThrowAsync<HttpRequestException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("example")]
    [InlineData("example/app/extra")]
    [InlineData("../app")]
    [InlineData("example/..")]
    [InlineData("example/.")]
    [InlineData("./app")]
    [InlineData(".")]
    [InlineData("../etc/passwd")]
    [InlineData("example/app?x=1")]
    [InlineData("example/app#frag")]
    [InlineData("exa mple/app")]
    [InlineData("exämple/app")]
    [InlineData("example//app")]
    [InlineData("-/app")]
    public async Task A_name_that_is_not_a_repository_never_reaches_a_url(string name)
    {
        var read = () => ListAsync(Source(), name);

        await read.Should().ThrowAsync<ArgumentException>();
        _handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("example/.github")]
    [InlineData("example/my.repo_name-2")]
    public async Task A_repository_name_with_dots_and_dashes_is_a_repository_name(string name)
    {
        List("[]", name);

        (await ListAsync(Source(), name)).Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData("../../../../admin/x")]
    [InlineData("abc")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF01")]
    [InlineData("")]
    public async Task A_commit_id_that_is_not_one_is_never_put_in_a_path_and_its_checks_are_unknown(string sha)
    {
        List($"[{Pull(1, sha: sha)}]");
        _handler.On("/repos/example/app/pulls/1", """{ "mergeable_state": "clean" }""");

        var list = await ListAsync(Source());

        list.Items.Single().Checks.Should().Be(ChecksState.Unknown);
        list.Items.Single().HeadSha.Should().BeEmpty();
        _handler.Requests.Should().OnlyContain(r => !r.RequestUri!.AbsolutePath.Contains("check-runs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sha256_commit_id_is_accepted()
    {
        var sha = new string('b', 64);
        List($"[{Pull(1, sha: sha)}]");
        _handler.On("/repos/example/app/pulls/1", """{ "mergeable_state": "clean" }""");
        _handler.On($"/repos/example/app/commits/{sha}/check-runs", """{ "check_runs": [ { "status": "completed", "conclusion": "success" } ] }""");

        (await ListAsync(Source())).Items.Single().HeadSha.Should().Be(sha);
    }

    [Theory]
    [InlineData("http://api.github.com/")]
    [InlineData("http://ghe.example/api/v3/")]
    [InlineData("ftp://api.github.com/")]
    public void An_api_address_that_is_not_https_stops_the_start_naming_the_key_not_the_value(string url)
    {
        var build = () => GitHubPullRequestSource.Create(new GitHubOptions { Token = "ghp_secret", ApiUrl = new Uri(url) }, _time);

        build.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("GitHub:ApiUrl").And.NotContain(url).And.NotContain("ghp_secret");
    }

    [Fact]
    public void A_site_address_that_is_not_https_stops_the_start()
    {
        var build = () => GitHubPullRequestSource.Create(new GitHubOptions { Token = "ghp_secret", WebUrl = new Uri("http://github.com/") }, _time);

        build.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("GitHub:WebUrl");
    }

    [Theory]
    [InlineData("MaxPullRequests")]
    [InlineData("MaxPages")]
    [InlineData("TimeoutSeconds")]
    public void A_setting_that_is_zero_or_negative_stops_the_start(string setting)
    {
        var options = new GitHubOptions { Token = "ghp_secret" };
        typeof(GitHubOptions).GetProperty(setting)!.SetValue(options, 0);

        var build = () => GitHubPullRequestSource.Create(options, _time);

        build.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(setting);
    }

    [Fact]
    public void A_loopback_address_can_be_http_and_an_address_without_a_trailing_slash_works()
    {
        var local = () => GitHubPullRequestSource.Create(new GitHubOptions { Token = "t", ApiUrl = new Uri("http://127.0.0.1:8080/api/v3") }, _time);

        local.Should().NotThrow();
    }

    [Fact]
    public async Task An_api_under_a_path_without_a_trailing_slash_is_reached_under_that_path()
    {
        _handler.On("/api/v3/repos/example/app/pulls", "[]");

        var list = await ListAsync(Source(o => o.ApiUrl = new Uri("https://ghe.example/api/v3")));

        list.Items.Should().BeEmpty();
        _handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/api/v3/repos/example/app/pulls");
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

            var read = () => ListAsync(source);

            (await read.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.Found);
            elsewhere.Requests.Should().Be(0);
        }
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Body, HttpStatusCode Status, string? Link, TimeSpan? RetryAfter, string? Remaining)> _routes = [];
        private readonly List<HttpRequestMessage> _requests = [];

        public IReadOnlyList<HttpRequestMessage> Requests => _requests;

        public void On(string path, string body, HttpStatusCode status = HttpStatusCode.OK, string? link = null, TimeSpan? retryAfter = null, string? remaining = null) =>
            _routes[path] = (body, status, link, retryAfter, remaining);

        public int Calls(string path) => _requests.Count(r => r.RequestUri!.AbsolutePath == path || r.RequestUri.PathAndQuery == path);

        // The route of the exact path and query, else of the path alone, else a 404.
        private (string Body, HttpStatusCode Status, string? Link, TimeSpan? RetryAfter, string? Remaining) Route(Uri uri)
        {
            if (_routes.TryGetValue(uri.PathAndQuery, out var exact))
            {
                return exact;
            }

            return _routes.TryGetValue(uri.AbsolutePath, out var byPath) ? byPath : ("{}", HttpStatusCode.NotFound, null, null, null);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Add(request);
            var uri = request.RequestUri!;
            var route = Route(uri);
            var response = new HttpResponseMessage(route.Item2) { Content = new StringContent(route.Item1, Encoding.UTF8, "application/json") };
            if (route.Item3 is not null)
            {
                response.Headers.TryAddWithoutValidation("Link", route.Item3);
            }

            if (route.Item4 is { } retryAfter)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
            }

            if (route.Item5 is not null)
            {
                response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", route.Item5);
            }

            return Task.FromResult(response);
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

                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("[]"), _stop.Token);
                        context.Response.Close();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
                    {
                        return;
                    }
                }
            }, _stop.Token);
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
