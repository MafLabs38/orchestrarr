using System.Net;
using System.Text;
using Orchestrarr.Core.Forge;

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
}
