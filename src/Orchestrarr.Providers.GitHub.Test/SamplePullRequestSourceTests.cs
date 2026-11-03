using Orchestrarr.Core.Forge;

namespace Orchestrarr.Providers.GitHub.Test;

public sealed class SamplePullRequestSourceTests
{
    private static readonly string File = Path.Combine(AppContext.BaseDirectory, "github-pull-requests.sample.json");

    [Fact]
    public async Task The_pull_requests_of_a_repository_are_read_from_the_file_with_their_states()
    {
        var source = new SamplePullRequestSource(File);

        var pullRequests = await source.ListDependencyPullRequestsAsync("example/billing-api", TestContext.Current.CancellationToken);

        source.Kind.Should().Be("sample");
        source.Configured.Should().BeTrue();
        pullRequests.Should().HaveCount(7);
        pullRequests.Should().Contain(p => p.Checks == ChecksState.Failing && p.Merge == MergeState.Unstable);
        pullRequests.Should().Contain(p => p.Merge == MergeState.Conflicting);
        pullRequests.First().Changes.Should().ContainSingle().Which.Kind.Should().Be(UpdateKind.Patch);
    }

    [Fact]
    public async Task A_repository_that_is_not_in_the_file_has_no_pull_requests()
    {
        var pullRequests = await new SamplePullRequestSource(File).ListDependencyPullRequestsAsync("example/unknown", TestContext.Current.CancellationToken);

        pullRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_file_is_an_error_not_an_empty_list()
    {
        var read = () => new SamplePullRequestSource(Path.Combine(AppContext.BaseDirectory, "nope.json")).ListDependencyPullRequestsAsync("example/app", TestContext.Current.CancellationToken);

        await read.Should().ThrowAsync<FileNotFoundException>();
    }
}
