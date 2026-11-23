using System.Net;
using System.Text;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Providers.GitHub.Test;

public sealed class GitHubForgeActsTests
{
    private static readonly string Head = new('a', 40);
    private static readonly string Merge = new('b', 40);

    private sealed class Recorder(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static GitHubForgeActs Acts(Recorder handler) => GitHubForgeActs.Create(new GitHubOptions { Token = "ghp_secret" }, handler);

    [Fact]
    public async Task A_merge_is_a_squash_on_the_head_that_was_authorised_and_gives_the_commit_it_made()
    {
        var handler = new Recorder(HttpStatusCode.OK, $$"""{ "sha": "{{Merge}}", "merged": true }""");

        var result = await Acts(handler).MergePullRequestAsync("example/shop", 7, Head, TestContext.Current.CancellationToken);

        result.Should().Be(new MergeResult(true, Merge, null));
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Put);
        request.Path.Should().Be("/repos/example/shop/pulls/7/merge");
        request.Body.Should().Contain(Head).And.Contain("squash");
    }

    [Fact]
    public async Task A_head_that_moved_is_refused_by_the_forge_and_said()
    {
        var handler = new Recorder(HttpStatusCode.Conflict, """{ "message": "Head branch was modified. Review and try the merge again." }""");

        var result = await Acts(handler).MergePullRequestAsync("example/shop", 7, Head, TestContext.Current.CancellationToken);

        result.Merged.Should().BeFalse();
        result.Error.Should().StartWith("409").And.Contain("Head branch was modified");
    }

    [Theory]
    [InlineData("../../etc", 7, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("example/shop", 0, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("example/shop", 7, "main")]
    public async Task What_is_not_well_formed_is_refused_before_any_request(string repository, int number, string sha)
    {
        var handler = new Recorder(HttpStatusCode.OK, "{}");

        var result = await Acts(handler).MergePullRequestAsync(repository, number, sha, TestContext.Current.CancellationToken);

        result.Merged.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_forge_that_cannot_be_reached_is_a_refusal_not_an_exception()
    {
        var handler = new Throwing();

        var result = await GitHubForgeActs.Create(new GitHubOptions { Token = "ghp_secret" }, handler).MergePullRequestAsync("example/shop", 7, Head, TestContext.Current.CancellationToken);

        result.Merged.Should().BeFalse();
        result.Error.Should().Contain("did not answer").And.Contain("may have been made");
    }

    [Theory]
    [InlineData(HttpStatusCode.MethodNotAllowed, """{ "message": "Pull Request is not mergeable" }""")]
    [InlineData(HttpStatusCode.OK, "not json at all")]
    [InlineData(HttpStatusCode.OK, """{ "merged": true }""")]
    public async Task An_answer_that_does_not_say_which_commit_the_merge_made_is_never_a_merge(HttpStatusCode status, string body)
    {
        var result = await Acts(new Recorder(status, body)).MergePullRequestAsync("example/shop", 7, Head, TestContext.Current.CancellationToken);

        result.Merged.Should().BeFalse();
    }

    [Fact]
    public async Task A_redirect_is_not_followed_and_the_token_goes_in_the_authorization_header_only()
    {
        var handler = new Recorder(HttpStatusCode.Redirect, "{}");

        var result = await Acts(handler).MergePullRequestAsync("example/shop", 7, Head, TestContext.Current.CancellationToken);

        result.Merged.Should().BeFalse();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public void Without_a_token_there_is_no_act()
    {
        var create = () => GitHubForgeActs.Create(new GitHubOptions());

        create.Should().Throw<InvalidOperationException>();
    }

    private sealed class Throwing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("down");
    }

    // A forge in a few lines: what each call answers, in the order that the calls are made
    private sealed class Script : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

        public Dictionary<string, (HttpStatusCode Status, string Body)> Answers { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            var (status, body) = Answers.GetValueOrDefault($"{request.Method} {path}", (HttpStatusCode.NotFound, """{ "message": "Not Found" }"""));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly string Tip = new('1', 40);
    private static readonly string Merged = new('2', 40);
    private static readonly string TreeOfMerge = new('3', 40);
    private static readonly string NewTree = new('4', 40);
    private static readonly string Repair = new('5', 40);

    private static Script Forge()
    {
        var script = new Script();
        script.Answers["GET /repos/example/shop/git/ref/heads/develop"] = (HttpStatusCode.OK, $$"""{ "object": { "sha": "{{Tip}}" } }""");
        script.Answers["POST /repos/example/shop/git/refs"] = (HttpStatusCode.Created, "{}");
        script.Answers["POST /repos/example/shop/merges"] = (HttpStatusCode.Created, $$"""{ "sha": "{{Merged}}" }""");
        script.Answers[$"GET /repos/example/shop/git/commits/{Merged}"] = (HttpStatusCode.OK, $$"""{ "tree": { "sha": "{{TreeOfMerge}}" } }""");
        script.Answers["POST /repos/example/shop/git/trees"] = (HttpStatusCode.Created, $$"""{ "sha": "{{NewTree}}" }""");
        script.Answers["POST /repos/example/shop/git/commits"] = (HttpStatusCode.Created, $$"""{ "sha": "{{Repair}}" }""");
        script.Answers["PATCH /repos/example/shop/git/refs/heads/orchestrarr/p-1-abc123"] = (HttpStatusCode.OK, "{}");
        script.Answers["POST /repos/example/shop/pulls"] = (HttpStatusCode.Created, """{ "number": 17 }""");
        return script;
    }

    private static readonly IReadOnlyList<PatchedFile> Files = [new PatchedFile("server.js", "fixed();")];

    [Fact]
    public async Task A_repair_is_a_branch_from_the_base_with_the_change_merged_in_and_one_commit_on_top_and_a_pull_request()
    {
        var script = Forge();

        var opened = await GitHubForgeActs.Create(new GitHubOptions { Token = "ghp_secret" }, script).OpenPullRequestAsync("example/shop", "develop", Head, "orchestrarr/p-1-abc123", Files, "fix: repair #6", "body", TestContext.Current.CancellationToken);

        opened.Should().Be(new OpenedPullRequest(true, 17, Repair, null));
        script.Requests.Select(request => $"{request.Method} {request.Path}").Should().Equal(
            "GET /repos/example/shop/git/ref/heads/develop", "POST /repos/example/shop/git/refs", "POST /repos/example/shop/merges", $"GET /repos/example/shop/git/commits/{Merged}", "POST /repos/example/shop/git/trees",
            "POST /repos/example/shop/git/commits", "PATCH /repos/example/shop/git/refs/heads/orchestrarr/p-1-abc123", "POST /repos/example/shop/pulls");
        script.Requests.Single(request => request.Path.EndsWith("/merges", StringComparison.Ordinal)).Body.Should().Contain(Head);
        script.Requests.Single(request => request.Path.EndsWith("/git/trees", StringComparison.Ordinal)).Body.Should().Contain(TreeOfMerge).And.Contain("server.js").And.Contain("fixed();");
        script.Requests.Single(request => request.Path.EndsWith("/git/commits", StringComparison.Ordinal) && request.Method == HttpMethod.Post).Body.Should().Contain(Merged, "the repair is on top of the merge");
    }

    [Fact]
    public async Task A_conflict_with_the_base_or_a_name_that_exists_stops_the_opening_and_says_why()
    {
        var conflict = Forge();
        conflict.Answers["POST /repos/example/shop/merges"] = (HttpStatusCode.Conflict, """{ "message": "Merge conflict" }""");
        var exists = Forge();
        exists.Answers["POST /repos/example/shop/git/refs"] = (HttpStatusCode.UnprocessableEntity, """{ "message": "Reference already exists" }""");

        var first = await GitHubForgeActs.Create(new GitHubOptions { Token = "ghp_secret" }, conflict).OpenPullRequestAsync("example/shop", "develop", Head, "orchestrarr/p-1-abc123", Files, "t", "b", TestContext.Current.CancellationToken);
        var second = await GitHubForgeActs.Create(new GitHubOptions { Token = "ghp_secret" }, exists).OpenPullRequestAsync("example/shop", "develop", Head, "orchestrarr/p-1-abc123", Files, "t", "b", TestContext.Current.CancellationToken);

        first.Opened.Should().BeFalse();
        first.Error.Should().Contain("conflict").And.Contain("409");
        second.Opened.Should().BeFalse();
        second.Error.Should().Contain("Reference already exists");
        conflict.Requests.Should().NotContain(request => request.Path.EndsWith("/pulls", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../../etc", "develop", "orchestrarr/p-1-abc123")]
    [InlineData("example/shop", "develop", "main")]
    [InlineData("example/shop", "develop", "orchestrarr/../main")]
    [InlineData("example/shop", "", "orchestrarr/p-1-abc123")]
    public async Task What_is_not_well_formed_opens_nothing(string repository, string baseBranch, string branch)
    {
        var script = Forge();

        var opened = await GitHubForgeActs.Create(new GitHubOptions { Token = "ghp_secret" }, script).OpenPullRequestAsync(repository, baseBranch, Head, branch, Files, "t", "b", TestContext.Current.CancellationToken);

        opened.Opened.Should().BeFalse();
        script.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_pull_request_that_another_replaces_is_closed_with_a_comment()
    {
        var script = new Script();
        script.Answers["POST /repos/example/shop/issues/6/comments"] = (HttpStatusCode.Created, "{}");
        script.Answers["PATCH /repos/example/shop/pulls/6"] = (HttpStatusCode.OK, "{}");

        var closed = await GitHubForgeActs.Create(new GitHubOptions { Token = "ghp_secret" }, script).ClosePullRequestAsync("example/shop", 6, "Replaced by #17", TestContext.Current.CancellationToken);

        closed.Should().BeTrue();
        script.Requests.Select(request => $"{request.Method} {request.Path}").Should().Equal("POST /repos/example/shop/issues/6/comments", "PATCH /repos/example/shop/pulls/6");
        script.Requests[1].Body.Should().Contain("closed");
        (await GitHubForgeActs.Create(new GitHubOptions { Token = "ghp_secret" }, new Script()).ClosePullRequestAsync("../x", 6, "c", TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task A_pull_request_is_opened_from_the_branch_of_an_agent_only_at_the_commit_that_was_audited()
    {
        var script = new Script();
        script.Answers["GET /repos/example/shop/git/ref/heads/agent/p-1-abc"] = (HttpStatusCode.OK, $$"""{ "object": { "sha": "{{Head}}" } }""");
        script.Answers["POST /repos/example/shop/pulls"] = (HttpStatusCode.Created, $$"""{ "number": 9, "head": { "sha": "{{Head}}" } }""");

        var opened = await GitHubForgeActs.Create(new GitHubOptions { Token = "ghp_secret" }, script).OpenPullRequestFromBranchAsync("example/shop", "develop", "agent/p-1-abc", Head, "fix", "body", TestContext.Current.CancellationToken);

        opened.Should().Be(new OpenedPullRequest(true, 9, Head, null));
        script.Requests.Select(request => $"{request.Method} {request.Path}").Should().Equal("GET /repos/example/shop/git/ref/heads/agent/p-1-abc", "POST /repos/example/shop/pulls");
        script.Requests[1].Body.Should().Contain("\"head\":\"agent/p-1-abc\"").And.Contain("\"base\":\"develop\"");
    }

    [Fact]
    public async Task A_branch_that_moved_or_a_name_that_is_not_an_agents_opens_nothing()
    {
        var script = new Script();
        script.Answers["GET /repos/example/shop/git/ref/heads/agent/p-1-abc"] = (HttpStatusCode.OK, $$"""{ "object": { "sha": "{{Merge}}" } }""");
        var acts = GitHubForgeActs.Create(new GitHubOptions { Token = "ghp_secret" }, script);
        var token = TestContext.Current.CancellationToken;

        (await acts.OpenPullRequestFromBranchAsync("example/shop", "develop", "agent/p-1-abc", Head, "t", "b", token)).Opened.Should().BeFalse();
        (await acts.OpenPullRequestFromBranchAsync("example/shop", "develop", "main", Head, "t", "b", token)).Opened.Should().BeFalse();
        (await acts.OpenPullRequestFromBranchAsync("../x", "develop", "agent/p-1-abc", Head, "t", "b", token)).Opened.Should().BeFalse();
        script.Requests.Should().ContainSingle("only the read of the branch was made, no pull request");
    }

    [Fact]
    public async Task A_pull_request_that_opened_on_another_commit_than_the_audited_one_is_closed_and_not_reported_as_opened()
    {
        var script = new Script();
        script.Answers["GET /repos/example/shop/git/ref/heads/agent/p-1-abc"] = (HttpStatusCode.OK, $$"""{ "object": { "sha": "{{Head}}" } }""");
        script.Answers["POST /repos/example/shop/pulls"] = (HttpStatusCode.Created, $$"""{ "number": 9, "head": { "sha": "{{Merge}}" } }""");
        script.Answers["PATCH /repos/example/shop/pulls/9"] = (HttpStatusCode.OK, "{}");

        var opened = await GitHubForgeActs.Create(new GitHubOptions { Token = "ghp_secret" }, script).OpenPullRequestFromBranchAsync("example/shop", "develop", "agent/p-1-abc", Head, "t", "b", TestContext.Current.CancellationToken);

        opened.Opened.Should().BeFalse();
        opened.Error.Should().Contain("moved");
        script.Requests.Select(request => $"{request.Method} {request.Path}").Should().EndWith("PATCH /repos/example/shop/pulls/9");
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{ \"message\": \"Validation Failed\" }")]
    [InlineData(HttpStatusCode.Forbidden, "{ \"message\": \"Resource not accessible\" }")]
    [InlineData(HttpStatusCode.Created, "{ \"id\": 1 }")]
    public async Task A_pull_request_that_the_forge_refuses_or_answers_with_no_number_is_not_opened(HttpStatusCode status, string body)
    {
        var script = new Script();
        script.Answers["GET /repos/example/shop/git/ref/heads/agent/p-1-abc"] = (HttpStatusCode.OK, $$"""{ "object": { "sha": "{{Head}}" } }""");
        script.Answers["POST /repos/example/shop/pulls"] = (status, body);

        var opened = await GitHubForgeActs.Create(new GitHubOptions { Token = "ghp_secret" }, script).OpenPullRequestFromBranchAsync("example/shop", "develop", "agent/p-1-abc", Head, "t", "b", TestContext.Current.CancellationToken);

        opened.Opened.Should().BeFalse();
        script.Requests.Select(request => request.Method.Method).Should().Equal("GET", "POST");
    }

    private sealed class Queued : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> _answers = [];

        public List<string> Requests { get; } = [];

        public void Then(string key, HttpStatusCode status, string body)
        {
            if (!_answers.TryGetValue(key, out var queue))
            {
                _answers[key] = queue = new Queue<(HttpStatusCode, string)>();
            }
            queue.Enqueue((status, body));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = $"{request.Method} {request.RequestUri!.AbsolutePath}";
            Requests.Add(key);
            (HttpStatusCode Status, string Body) answer = (HttpStatusCode.NotFound, "{}");
            if (_answers.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                // The last answer of a key is the one that stays
                answer = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            }
            var (status, body) = answer;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static Queued ReadyBranch()
    {
        var queued = new Queued();
        queued.Then("GET /repos/example/shop/git/ref/heads/agent/p-1-abc", HttpStatusCode.OK, $$"""{ "object": { "sha": "{{Head}}" } }""");
        return queued;
    }

    [Fact]
    public async Task A_server_error_of_the_forge_is_tried_again_and_the_pull_request_that_the_forge_may_have_made_is_looked_for_first()
    {
        var queued = ReadyBranch();
        queued.Then("POST /repos/example/shop/pulls", HttpStatusCode.InternalServerError, "{}");
        queued.Then("POST /repos/example/shop/pulls", HttpStatusCode.Created, $$"""{ "number": 9, "head": { "sha": "{{Head}}" } }""");
        queued.Then("GET /repos/example/shop/pulls", HttpStatusCode.OK, "[]");

        var opened = await GitHubForgeActs.Create(new GitHubOptions { Token = "ghp_secret" }, queued, TimeSpan.Zero).OpenPullRequestFromBranchAsync("example/shop", "develop", "agent/p-1-abc", Head, "t", "b", TestContext.Current.CancellationToken);

        opened.Should().Be(new OpenedPullRequest(true, 9, Head, null));
        queued.Requests.Should().Equal("GET /repos/example/shop/git/ref/heads/agent/p-1-abc", "POST /repos/example/shop/pulls", "GET /repos/example/shop/pulls", "POST /repos/example/shop/pulls");
    }

    [Fact]
    public async Task A_pull_request_that_was_made_before_the_answer_was_lost_is_found_and_not_made_twice()
    {
        var queued = ReadyBranch();
        queued.Then("POST /repos/example/shop/pulls", HttpStatusCode.BadGateway, "{}");
        queued.Then("GET /repos/example/shop/pulls", HttpStatusCode.OK, $$"""[ { "number": 12, "head": { "sha": "{{Head}}" } } ]""");

        var opened = await GitHubForgeActs.Create(new GitHubOptions { Token = "ghp_secret" }, queued, TimeSpan.Zero).OpenPullRequestFromBranchAsync("example/shop", "develop", "agent/p-1-abc", Head, "t", "b", TestContext.Current.CancellationToken);

        opened.Should().Be(new OpenedPullRequest(true, 12, Head, null));
        queued.Requests.Count(request => request.StartsWith("POST", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task A_forge_that_keeps_failing_gives_up_after_three_tries_and_says_so()
    {
        var queued = ReadyBranch();
        queued.Then("POST /repos/example/shop/pulls", HttpStatusCode.InternalServerError, "{}");
        queued.Then("GET /repos/example/shop/pulls", HttpStatusCode.OK, "[]");

        var opened = await GitHubForgeActs.Create(new GitHubOptions { Token = "ghp_secret" }, queued, TimeSpan.Zero).OpenPullRequestFromBranchAsync("example/shop", "develop", "agent/p-1-abc", Head, "t", "b", TestContext.Current.CancellationToken);

        opened.Opened.Should().BeFalse();
        opened.Error.Should().Contain("500");
        queued.Requests.Count(request => request.StartsWith("POST", StringComparison.Ordinal)).Should().Be(3);
    }
}
