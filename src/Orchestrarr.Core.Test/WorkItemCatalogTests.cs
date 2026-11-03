using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.WorkItems;
using static Orchestrarr.Core.Test.FleetBuilders;

namespace Orchestrarr.Core.Test;

public sealed class WorkItemCatalogTests
{
    private static readonly WorkItemCatalogOptions Options = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly IFleetSource _source = Substitute.For<IFleetSource>();
    private readonly WorkItemCatalog _catalog;

    public WorkItemCatalogTests()
    {
        _source.Kind.Returns("test");
        _source.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Snapshot([Repository(pullRequests: 1)])));
        _catalog = new WorkItemCatalog(_source, new NoPowers(), _time, Options, NullLogger<WorkItemCatalog>.Instance);
    }

    private static Task<FleetSnapshot> Fail(Exception exception) => Task.FromException<FleetSnapshot>(exception);

    [Fact]
    public async Task The_source_is_read_once_within_the_cache_duration()
    {
        await _catalog.GetAsync(refresh: false, CancellationToken.None);
        _time.Advance(Options.CacheDuration - TimeSpan.FromSeconds(1));
        await _catalog.GetAsync(refresh: false, CancellationToken.None);

        await _source.Received(1).ReadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_source_is_read_again_once_the_cache_duration_has_passed()
    {
        await _catalog.GetAsync(refresh: false, CancellationToken.None);
        _time.Advance(Options.CacheDuration);
        await _catalog.GetAsync(refresh: false, CancellationToken.None);

        await _source.Received(2).ReadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refresh_reads_the_source_again_within_the_cache_duration_but_not_more_often_than_the_minimum_interval()
    {
        await _catalog.GetAsync(refresh: false, CancellationToken.None);
        _time.Advance(Options.MinRefreshInterval);
        await _catalog.GetAsync(refresh: true, CancellationToken.None);
        await _catalog.GetAsync(refresh: true, CancellationToken.None);

        await _source.Received(2).ReadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failing_source_after_a_good_read_keeps_the_items_marks_them_stale_and_gives_a_code_not_the_message()
    {
        await _catalog.GetAsync(refresh: false, CancellationToken.None);
        _time.Advance(Options.CacheDuration);
        _source.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => Fail(new HttpRequestException("Connection refused (portarr.internal:8484)")));

        var report = await _catalog.GetAsync(refresh: false, CancellationToken.None);

        report.Items.Should().HaveCount(1);
        report.Stale.Should().BeTrue();
        report.Error.Should().Be(SourceError.Unreachable);
    }

    [Fact]
    public async Task A_failing_source_at_first_gives_an_empty_report_with_the_code()
    {
        _source.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => Fail(new HttpRequestException("no route")));

        var report = await _catalog.GetAsync(refresh: false, CancellationToken.None);

        report.Items.Should().BeEmpty();
        report.Error.Should().Be(SourceError.Unreachable);
        report.Stale.Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_read_is_not_retried_by_every_request_but_after_the_cache_duration()
    {
        _source.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => Fail(new HttpRequestException("down")));

        await _catalog.GetAsync(refresh: false, CancellationToken.None);
        await _catalog.GetAsync(refresh: false, CancellationToken.None);
        await _catalog.GetAsync(refresh: false, CancellationToken.None);
        _time.Advance(Options.CacheDuration);
        await _catalog.GetAsync(refresh: false, CancellationToken.None);

        await _source.Received(2).ReadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_good_read_after_a_failure_clears_the_error_and_the_stale_mark()
    {
        await _catalog.GetAsync(refresh: false, CancellationToken.None);
        _time.Advance(Options.CacheDuration);
        _source.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => Fail(new HttpRequestException("down")));
        await _catalog.GetAsync(refresh: false, CancellationToken.None);
        _time.Advance(Options.CacheDuration);
        _source.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Snapshot([Repository("example/other", pullRequests: 2)])));

        var report = await _catalog.GetAsync(refresh: false, CancellationToken.None);

        report.Error.Should().BeNull();
        report.Stale.Should().BeFalse();
        report.Items.Should().ContainSingle().Which.Title.Should().Be("example/other");
    }

    [Fact]
    public async Task A_cancelled_request_is_not_a_source_error_and_does_not_poison_the_cache()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _source.ReadAsync(Arg.Any<CancellationToken>()).Returns(call => Task.FromCanceled<FleetSnapshot>(call.Arg<CancellationToken>()));

        var cancelledRead = () => _catalog.GetAsync(refresh: false, cancelled.Token);
        await cancelledRead.Should().ThrowAsync<OperationCanceledException>();

        _source.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Snapshot([Repository(pullRequests: 1)])));
        var report = await _catalog.GetAsync(refresh: false, CancellationToken.None);
        report.Error.Should().BeNull();
        report.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task Requests_arriving_while_the_source_is_being_read_share_that_read()
    {
        var release = new TaskCompletionSource<FleetSnapshot>();
        _source.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => release.Task);

        var first = _catalog.GetAsync(refresh: false, CancellationToken.None);
        var second = _catalog.GetAsync(refresh: false, CancellationToken.None);
        release.SetResult(Snapshot([Repository(pullRequests: 1)]));
        await Task.WhenAll(first, second);

        await _source.Received(1).ReadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_work_item_is_found_by_its_id_whatever_the_case_and_an_unknown_id_is_not()
    {
        var found = await _catalog.FindAsync("REPOSITORY:example/app", CancellationToken.None);
        var missing = await _catalog.FindAsync("repository:example/other", CancellationToken.None);

        found.Should().NotBeNull();
        missing.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SourceError.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, SourceError.Unauthorized)]
    [InlineData(HttpStatusCode.Found, SourceError.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError, SourceError.Unreachable)]
    [InlineData(null, SourceError.Unreachable)]
    public void An_http_failure_is_classified_by_its_status(HttpStatusCode? status, SourceError expected)
    {
        SourceErrors.From(new HttpRequestException("x", null, status)).Should().Be(expected);
    }

    [Fact]
    public void A_timeout_a_malformed_report_and_anything_else_have_their_own_code()
    {
        SourceErrors.From(new TaskCanceledException()).Should().Be(SourceError.Timeout);
        SourceErrors.From(new JsonException()).Should().Be(SourceError.InvalidResponse);
        SourceErrors.From(new InvalidOperationException()).Should().Be(SourceError.InvalidResponse);
        SourceErrors.From(new FileNotFoundException()).Should().Be(SourceError.Failed);
    }
}
