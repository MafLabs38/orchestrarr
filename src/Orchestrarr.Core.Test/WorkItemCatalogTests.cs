using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.WorkItems;
using static Orchestrarr.Core.Test.FleetBuilders;

namespace Orchestrarr.Core.Test;

public sealed class WorkItemCatalogTests
{
    private readonly FakeTimeProvider _time = new(Now);
    private readonly IFleetSource _source = Substitute.For<IFleetSource>();
    private readonly WorkItemCatalog _catalog;

    public WorkItemCatalogTests()
    {
        _source.Kind.Returns("test");
        _source.ReadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Snapshot([Repository(pullRequests: 1)])));
        _catalog = new WorkItemCatalog(_source, _time);
    }

    [Fact]
    public async Task The_source_is_read_once_within_the_cache_duration()
    {
        await _catalog.GetAsync(refresh: false, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(29));
        await _catalog.GetAsync(refresh: false, CancellationToken.None);

        await _source.Received(1).ReadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_source_is_read_again_after_the_cache_duration_or_when_asked()
    {
        await _catalog.GetAsync(refresh: false, CancellationToken.None);
        _time.Advance(WorkItemCatalog.CacheDuration + TimeSpan.FromSeconds(1));
        await _catalog.GetAsync(refresh: false, CancellationToken.None);
        await _catalog.GetAsync(refresh: true, CancellationToken.None);

        await _source.Received(3).ReadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failing_source_after_a_good_read_keeps_the_items_and_marks_them_stale()
    {
        await _catalog.GetAsync(refresh: false, CancellationToken.None);
        _source.ReadAsync(Arg.Any<CancellationToken>()).Returns<Task<FleetSnapshot>>(_ => throw new HttpRequestException("Portarr is down"));

        var report = await _catalog.GetAsync(refresh: true, CancellationToken.None);

        report.Items.Should().HaveCount(1);
        report.Stale.Should().BeTrue();
        report.Error.Should().Be("Portarr is down");
    }

    [Fact]
    public async Task A_failing_source_at_first_gives_an_empty_report_with_the_error_and_is_retried_at_once()
    {
        _source.ReadAsync(Arg.Any<CancellationToken>()).Returns<Task<FleetSnapshot>>(_ => throw new HttpRequestException("no route"));

        var first = await _catalog.GetAsync(refresh: false, CancellationToken.None);
        await _catalog.GetAsync(refresh: false, CancellationToken.None);

        first.Items.Should().BeEmpty();
        first.Error.Should().Be("no route");
        first.Stale.Should().BeFalse();
        await _source.Received(2).ReadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_work_item_is_found_by_its_id_whatever_the_case_and_an_unknown_id_is_not()
    {
        var found = await _catalog.FindAsync("REPOSITORY:example/app", CancellationToken.None);
        var missing = await _catalog.FindAsync("repository:example/other", CancellationToken.None);

        found.Should().NotBeNull();
        missing.Should().BeNull();
    }
}
