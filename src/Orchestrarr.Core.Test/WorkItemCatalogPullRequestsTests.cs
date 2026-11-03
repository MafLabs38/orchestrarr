using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.WorkItems;
using static Orchestrarr.Core.Test.FleetBuilders;

namespace Orchestrarr.Core.Test;

public sealed class WorkItemCatalogPullRequestsTests
{
    private readonly FakeTimeProvider _time = new(Now);
    private readonly IFleetSource _source = Substitute.For<IFleetSource>();
    private readonly IPullRequestSource _forge = Substitute.For<IPullRequestSource>();

    private static PullRequestInfo PullRequest(int number) => new(
        number, $"Bump a from 1.0.0 to 2.0.0 ({number})", new Uri($"https://forge.example/o/r/pull/{number}"), "bot", "bot/a", false,
        ChecksState.Passing, MergeState.Clean, [], [new DependencyChange("a", "1.0.0", "2.0.0", UpdateKind.Major)], Now);

    private WorkItemCatalog Catalog(params RepositoryState[] repositories)
    {
        _source.Kind.Returns("test");
        _source.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Snapshot(repositories)));
        _forge.Configured.Returns(true);
        _forge.Kind.Returns("fake");
        return new WorkItemCatalog(_source, _forge, new NoPowers(), _time, new WorkItemCatalogOptions(), NullLogger<WorkItemCatalog>.Instance);
    }

    [Fact]
    public async Task A_work_item_with_pull_requests_waiting_gets_them_from_the_forge()
    {
        _forge.ListDependencyPullRequestsAsync("example/app", Arg.Any<CancellationToken>()).Returns([PullRequest(1), PullRequest(2)]);

        var report = await Catalog(Repository(pullRequests: 2)).GetAsync(refresh: false, CancellationToken.None);

        report.Items.Should().ContainSingle().Which.PullRequests!.Select(p => p.Number).Should().Equal(1, 2);
        report.Items[0].PullRequestsError.Should().BeNull();
    }

    [Fact]
    public async Task The_forge_is_only_asked_about_work_items_that_wait_for_pull_requests()
    {
        var report = await Catalog(Repository("example/a", secrets: 1), Repository("example/b", pullRequests: 1)).GetAsync(refresh: false, CancellationToken.None);

        await _forge.DidNotReceive().ListDependencyPullRequestsAsync("example/a", Arg.Any<CancellationToken>());
        await _forge.Received(1).ListDependencyPullRequestsAsync("example/b", Arg.Any<CancellationToken>());
        report.Items.Single(i => i.Title == "example/a").PullRequests.Should().BeNull();
    }

    [Fact]
    public async Task A_third_party_image_is_not_looked_up_in_the_forge()
    {
        _source.Kind.Returns("test");
        _source.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Snapshot(images: [Image("grafana/grafana:11", placements: Place("a"))])));
        _forge.Configured.Returns(true);
        var catalog = new WorkItemCatalog(_source, _forge, new NoPowers(), _time, new WorkItemCatalogOptions(), NullLogger<WorkItemCatalog>.Instance);

        await catalog.GetAsync(refresh: false, CancellationToken.None);

        await _forge.DidNotReceive().ListDependencyPullRequestsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_forge_that_fails_for_one_repository_says_so_on_that_work_item_only()
    {
        _forge.ListDependencyPullRequestsAsync("example/a", Arg.Any<CancellationToken>()).Returns(_ => Task.FromException<IReadOnlyList<PullRequestInfo>>(new HttpRequestException("secret detail", null, System.Net.HttpStatusCode.Forbidden)));
        _forge.ListDependencyPullRequestsAsync("example/b", Arg.Any<CancellationToken>()).Returns([PullRequest(7)]);

        var report = await Catalog(Repository("example/a", pullRequests: 1), Repository("example/b", pullRequests: 1)).GetAsync(refresh: false, CancellationToken.None);

        report.Error.Should().BeNull();
        var failed = report.Items.Single(i => i.Title == "example/a");
        failed.PullRequests.Should().BeNull();
        failed.PullRequestsError.Should().Be(SourceError.Unauthorized);
        report.Items.Single(i => i.Title == "example/b").PullRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task Without_a_forge_the_pull_requests_are_not_read_and_nothing_is_asked()
    {
        _forge.Configured.Returns(false);
        _source.Kind.Returns("test");
        _source.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Snapshot([Repository(pullRequests: 3)])));
        var catalog = new WorkItemCatalog(_source, _forge, new NoPowers(), _time, new WorkItemCatalogOptions(), NullLogger<WorkItemCatalog>.Instance);

        var report = await catalog.GetAsync(refresh: false, CancellationToken.None);

        report.Items.Should().ContainSingle().Which.PullRequests.Should().BeNull();
        await _forge.DidNotReceive().ListDependencyPullRequestsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        catalog.ForgeConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task A_cancelled_request_is_not_taken_for_a_forge_error()
    {
        using var cancelled = new CancellationTokenSource();
        _forge.ListDependencyPullRequestsAsync("example/app", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancelled.Cancel();
            return Task.FromCanceled<IReadOnlyList<PullRequestInfo>>(cancelled.Token);
        });

        var read = () => Catalog(Repository(pullRequests: 1)).GetAsync(refresh: false, cancelled.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
    }
}
