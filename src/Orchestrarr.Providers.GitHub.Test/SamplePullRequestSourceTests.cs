using Orchestrarr.Core.Forge;

namespace Orchestrarr.Providers.GitHub.Test;

public sealed class SamplePullRequestSourceTests
{
    private static readonly string File = Path.Combine(AppContext.BaseDirectory, "github-pull-requests.sample.json");

    [Fact]
    public async Task The_pull_requests_of_a_repository_are_read_from_the_file_with_their_states()
    {
        var source = new SamplePullRequestSource(File);

        var list = await source.ListDependencyPullRequestsAsync("example/billing-api", refresh: false, TestContext.Current.CancellationToken);
        var pullRequests = list.Items;

        pullRequests.Should().NotBeEmpty();
        pullRequests.Should().Contain(p => p.Checks == ChecksState.Failing && p.Merge == MergeState.Unstable);
        pullRequests.Should().Contain(p => p.Merge == MergeState.Conflicting);
        pullRequests.Should().OnlyContain(p => p.HeadSha.Length == 40);
    }

    [Fact]
    public async Task A_repository_that_is_not_in_the_file_has_no_pull_requests()
    {
        var list = await new SamplePullRequestSource(File).ListDependencyPullRequestsAsync("example/unknown", refresh: false, TestContext.Current.CancellationToken);

        list.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_file_is_an_error_not_an_empty_list()
    {
        var read = () => new SamplePullRequestSource(Path.Combine(AppContext.BaseDirectory, "nope.json")).ListDependencyPullRequestsAsync("example/app", refresh: false, TestContext.Current.CancellationToken);

        await read.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task A_malformed_file_is_an_error()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orchestrarr-{Guid.NewGuid():N}.json");
        await System.IO.File.WriteAllTextAsync(path, "{bad", TestContext.Current.CancellationToken);
        try
        {
            var read = () => new SamplePullRequestSource(path).ListDependencyPullRequestsAsync("example/app", refresh: false, TestContext.Current.CancellationToken);

            await read.Should().ThrowAsync<System.Text.Json.JsonException>();
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task An_empty_file_is_an_error_not_an_empty_forge()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orchestrarr-{Guid.NewGuid():N}.json");
        await System.IO.File.WriteAllTextAsync(path, "null", TestContext.Current.CancellationToken);
        try
        {
            var read = () => new SamplePullRequestSource(path).ListDependencyPullRequestsAsync("example/app", refresh: false, TestContext.Current.CancellationToken);

            await read.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
