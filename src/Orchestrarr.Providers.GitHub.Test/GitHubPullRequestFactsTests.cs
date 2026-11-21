using System.Net;
using System.Text;
using Orchestrarr.Core.Forge;

namespace Orchestrarr.Providers.GitHub.Test;

public sealed class GitHubPullRequestFactsTests
{
    private const string Head = "1111111111111111111111111111111111111111";
    private const string Base = "2222222222222222222222222222222222222222";
    private readonly Dictionary<string, string> _routes = new(StringComparer.Ordinal);

    private GitHubPullRequestFacts Facts() => GitHubPullRequestFacts.Create(new GitHubOptions { Token = "ghp_secret" }, new Handler(_routes));

    private void Serve(string checkRuns, string files = """[ { "filename": "package.json" }, { "filename": "package-lock.json" } ]""")
    {
        _routes["/repos/example/app/pulls/3"] = $$"""{ "draft": false, "head": { "sha": "{{Head}}" }, "base": { "sha": "{{Base}}" } }""";
        _routes["/repos/example/app/pulls/3/files"] = files;
        _routes[$"/repos/example/app/commits/{Head}/check-runs"] = checkRuns;
    }

    [Fact]
    public async Task A_pull_request_is_read_with_its_commits_its_paths_and_the_checks_of_its_head()
    {
        Serve("""{ "check_runs": [ { "status": "completed", "conclusion": "success" }, { "status": "completed", "conclusion": "skipped" } ] }""");

        var facts = await Facts().ReadAsync("example/app", 3, TestContext.Current.CancellationToken);

        facts.Should().NotBeNull();
        facts.HeadSha.Should().Be(Head);
        facts.BaseSha.Should().Be(Base);
        facts.Checks.Should().Be(ChecksState.Passing);
        facts.ChangedPaths.Should().Equal("package.json", "package-lock.json");
        facts.PathsComplete.Should().BeTrue();
    }

    [Theory]
    [InlineData("""{ "check_runs": [ { "status": "completed", "conclusion": "success" }, { "status": "in_progress", "conclusion": null } ] }""", ChecksState.Pending)]
    [InlineData("""{ "check_runs": [ { "status": "completed", "conclusion": "failure" } ] }""", ChecksState.Failing)]
    [InlineData("""{ "check_runs": [] }""", ChecksState.Unknown)]
    public async Task The_checks_are_passing_only_when_every_one_is_over_and_none_failed(string runs, ChecksState expected)
    {
        Serve(runs);

        (await Facts().ReadAsync("example/app", 3, TestContext.Current.CancellationToken))!.Checks.Should().Be(expected);
    }

    [Fact]
    public async Task What_cannot_be_read_is_null_and_a_name_that_is_not_a_repository_is_refused_before_any_request()
    {
        (await Facts().ReadAsync("example/app", 9, TestContext.Current.CancellationToken)).Should().BeNull();
        (await Facts().ReadAsync("../../etc", 3, TestContext.Current.CancellationToken)).Should().BeNull();
        (await Facts().ReadAsync("example/app", 0, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    private sealed class Handler(Dictionary<string, string> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(routes.TryGetValue(path, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
