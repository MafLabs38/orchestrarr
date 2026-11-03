using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.WorkItems;
using static Orchestrarr.Core.Test.FleetBuilders;

namespace Orchestrarr.Core.Test;

public sealed class WorkItemCatalogPullRequestsTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(Now);
    private readonly IFleetSource _source = Substitute.For<IFleetSource>();
    private readonly IPullRequestSource _forge = Substitute.For<IPullRequestSource>();
    private readonly WorkItemCatalogOptions _options = new() { ForgeParallelism = 2 };
    private WorkItemCatalog? _catalog;

    private static PullRequestInfo PullRequest(int number) => new(
        number, $"Bump a from 1.0.0 to 2.0.0 ({number})", new Uri($"https://forge.example/o/r/pull/{number}"), "bot", "bot/a", false,
        ChecksState.Passing, MergeState.Clean, [], [new DependencyChange("a", "1.0.0", "2.0.0", UpdateKind.Major)], Now, new string('a', 40));

    private static PullRequestList One(int number) => new([PullRequest(number)], Truncated: false);

    private WorkItemCatalog Catalog(params RepositoryState[] repositories) => Catalog(Snapshot(repositories));

    private WorkItemCatalog Catalog(FleetSnapshot fleet, bool forgeConfigured = true)
    {
        _source.Kind.Returns("test");
        _source.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(fleet));
        _forge.Configured.Returns(forgeConfigured);
        _forge.Kind.Returns("fake");
        _catalog = new WorkItemCatalog(_source, _forge, new NoPowers(), _time, _options, NullLogger<WorkItemCatalog>.Instance);
        return _catalog;
    }

    private void Serve(string repository, PullRequestList list) =>
        _forge.ListDependencyPullRequestsAsync(repository, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(list);

    private void Fail(string repository, Exception exception) =>
        _forge.ListDependencyPullRequestsAsync(repository, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromException<PullRequestList>(exception));

    public void Dispose() => _catalog?.Dispose();

    [Fact]
    public async Task A_work_item_with_pull_requests_waiting_gets_them_from_the_forge()
    {
        Serve("example/app", new PullRequestList([PullRequest(1), PullRequest(2)], Truncated: false));

        var report = await Catalog(Repository(pullRequests: 2)).GetAsync(refresh: false, CancellationToken.None);

        var read = report.Items.Should().ContainSingle().Which.PullRequests;
        read.Status.Should().Be(PullRequestsStatus.Read);
        read.Items.Select(p => p.Number).Should().Equal(1, 2);
        read.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task A_truncated_read_says_so()
    {
        Serve("example/app", new PullRequestList([PullRequest(1)], Truncated: true));

        var report = await Catalog(Repository(pullRequests: 41)).GetAsync(refresh: false, CancellationToken.None);

        report.Items.Single().PullRequests.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task The_forge_is_only_asked_about_work_items_that_wait_for_pull_requests()
    {
        Serve("example/b", One(1));

        var report = await Catalog(Repository("example/a", secrets: 1), Repository("example/b", pullRequests: 1)).GetAsync(refresh: false, CancellationToken.None);

        await _forge.DidNotReceive().ListDependencyPullRequestsAsync("example/a", Arg.Any<bool>(), Arg.Any<CancellationToken>());
        report.Items.Single(i => i.Title == "example/a").PullRequests.Status.Should().Be(PullRequestsStatus.NotNeeded);
        report.Items.Single(i => i.Title == "example/b").PullRequests.Status.Should().Be(PullRequestsStatus.Read);
    }

    [Fact]
    public async Task A_third_party_image_is_not_looked_up_in_the_forge()
    {
        var catalog = Catalog(Snapshot(images: [Image("grafana/grafana:11", placements: Place("a"))]));

        var report = await catalog.GetAsync(refresh: false, CancellationToken.None);

        await _forge.DidNotReceive().ListDependencyPullRequestsAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        report.Items.Single().PullRequests.Status.Should().Be(PullRequestsStatus.NotNeeded);
    }

    [Fact]
    public async Task Without_a_forge_the_pull_requests_are_not_connected_and_nothing_is_asked()
    {
        var catalog = Catalog(Snapshot([Repository(pullRequests: 3)]), forgeConfigured: false);

        var report = await catalog.GetAsync(refresh: false, CancellationToken.None);

        report.Items.Single().PullRequests.Status.Should().Be(PullRequestsStatus.NotConnected);
        await _forge.DidNotReceive().ListDependencyPullRequestsAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        catalog.ForgeConfigured.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, SourceError.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, SourceError.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, SourceError.Unreachable)]
    public async Task A_forge_that_fails_for_one_repository_says_so_on_that_work_item_only(HttpStatusCode status, SourceError expected)
    {
        Fail("example/a", new HttpRequestException("secret detail", null, status));
        Serve("example/b", One(7));

        var report = await Catalog(Repository("example/a", pullRequests: 1), Repository("example/b", pullRequests: 1)).GetAsync(refresh: false, CancellationToken.None);

        report.Error.Should().BeNull();
        var failed = report.Items.Single(i => i.Title == "example/a").PullRequests;
        failed.Status.Should().Be(PullRequestsStatus.Failed);
        failed.Error.Should().Be(expected);
        failed.Items.Should().BeEmpty();
        report.Items.Single(i => i.Title == "example/b").PullRequests.Status.Should().Be(PullRequestsStatus.Read);
    }

    [Fact]
    public async Task A_rate_limit_of_the_forge_has_its_own_code()
    {
        Fail("example/app", new ForgeRateLimitedException(HttpStatusCode.Forbidden, TimeSpan.FromMinutes(1)));

        var report = await Catalog(Repository(pullRequests: 1)).GetAsync(refresh: false, CancellationToken.None);

        report.Items.Single().PullRequests.Error.Should().Be(SourceError.RateLimited);
    }

    [Fact]
    public async Task A_timeout_of_the_forge_and_a_bad_repository_name_are_errors_of_that_work_item_not_of_the_report()
    {
        Fail("example/a", new TaskCanceledException());
        Fail("example/b", new ArgumentException("Not a repository name"));

        var report = await Catalog(Repository("example/a", pullRequests: 1), Repository("example/b", pullRequests: 1)).GetAsync(refresh: false, CancellationToken.None);

        report.Error.Should().BeNull();
        report.Items.Single(i => i.Title == "example/a").PullRequests.Error.Should().Be(SourceError.Timeout);
        report.Items.Single(i => i.Title == "example/b").PullRequests.Error.Should().Be(SourceError.Failed);
    }

    [Fact]
    public async Task A_failed_read_after_a_good_one_keeps_the_last_good_list_and_marks_it_stale()
    {
        Serve("example/app", One(1));
        var catalog = Catalog(Repository(pullRequests: 1));
        await catalog.GetAsync(refresh: false, CancellationToken.None);
        _time.Advance(_options.CacheDuration);
        Fail("example/app", new HttpRequestException("down"));

        var report = await catalog.GetAsync(refresh: false, CancellationToken.None);

        var read = report.Items.Single().PullRequests;
        read.Status.Should().Be(PullRequestsStatus.Failed);
        read.Error.Should().Be(SourceError.Unreachable);
        read.Stale.Should().BeTrue();
        read.Items.Select(p => p.Number).Should().Equal(1);
    }

    [Fact]
    public async Task A_refresh_asks_the_forge_to_read_again_and_a_plain_read_does_not()
    {
        Serve("example/app", One(1));
        var catalog = Catalog(Repository(pullRequests: 1));

        await catalog.GetAsync(refresh: false, CancellationToken.None);
        _time.Advance(_options.CacheDuration);
        await catalog.GetAsync(refresh: true, CancellationToken.None);

        await _forge.Received(1).ListDependencyPullRequestsAsync("example/app", false, Arg.Any<CancellationToken>());
        await _forge.Received(1).ListDependencyPullRequestsAsync("example/app", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_slow_forge_does_not_hold_the_report_which_answers_with_the_pull_requests_pending_then_gets_them()
    {
        var release = new TaskCompletionSource<PullRequestList>();
        _forge.ListDependencyPullRequestsAsync("example/app", Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(_ => release.Task);
        var catalog = Catalog(Repository(pullRequests: 1));

        var answer = catalog.GetAsync(refresh: false, CancellationToken.None);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _time.Advance(_options.ForgeFirstWait);
        var report = await answer;

        report.Items.Single().PullRequests.Status.Should().Be(PullRequestsStatus.Pending);
        release.SetResult(One(3));
        await catalog.EnrichmentIdle;
        catalog.Peek()!.Items.Single().PullRequests.Items.Select(p => p.Number).Should().Equal(3);
    }

    [Fact]
    public async Task What_the_forge_has_not_answered_when_the_time_budget_is_spent_has_timed_out()
    {
        _forge.ListDependencyPullRequestsAsync("example/app", Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            return new PullRequestList([], false);
        });
        var catalog = Catalog(Repository(pullRequests: 1));

        var answer = catalog.GetAsync(refresh: false, CancellationToken.None);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _time.Advance(_options.ForgeTimeBudget);
        await catalog.EnrichmentIdle;
        await answer;

        var read = catalog.Peek()!.Items.Single().PullRequests;
        read.Status.Should().Be(PullRequestsStatus.Failed);
        read.Error.Should().Be(SourceError.Timeout);
    }

    [Fact]
    public async Task Only_a_bounded_number_of_repositories_are_read_from_the_forge_at_once()
    {
        var running = 0;
        var peak = 0;
        _forge.ListDependencyPullRequestsAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(30);
            Interlocked.Decrement(ref running);
            return new PullRequestList([], false);
        });
        var repositories = Enumerable.Range(1, 8).Select(i => Repository($"example/r{i}", pullRequests: 1)).ToArray();

        await Catalog(repositories).GetAsync(refresh: false, CancellationToken.None);
        await _catalog!.EnrichmentIdle;

        peak.Should().BeInRange(1, _options.ForgeParallelism);
    }

    [Fact]
    public async Task The_catalog_can_be_peeked_without_reading_anything()
    {
        var catalog = Catalog(Repository(pullRequests: 1));

        catalog.Peek().Should().BeNull();
        await _source.DidNotReceive().ReadAsync(Arg.Any<CancellationToken>());

        Serve("example/app", One(1));
        await catalog.GetAsync(refresh: false, CancellationToken.None);
        catalog.Peek().Should().NotBeNull();
    }

    [Fact]
    public async Task A_cancelled_request_is_not_taken_for_a_forge_error()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var catalog = Catalog(Repository(pullRequests: 1));

        var read = () => catalog.GetAsync(refresh: false, cancelled.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
        }
        while (value > current && Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
