using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Infrastructure;

namespace Orchestrarr.Core.Test.Infrastructure;

public sealed class InfrastructureCatalogTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
    private readonly InfrastructureCatalogOptions _options = new() { CacheDuration = TimeSpan.FromMinutes(5), MinRefreshInterval = TimeSpan.FromMinutes(1), FirstWait = TimeSpan.FromSeconds(8) };
    private static readonly DescriptionScope Scope = new([], [], [new RepositoryScope("example/infra", "kubernetes", ["k8s/**/*.yaml"])]);

    private static InfrastructureSnapshot Snapshot() =>
        new(DateTimeOffset.UnixEpoch, [new InfrastructureFile("example/infra", "k8s/a.yaml", "kubernetes", "image: a:1\n")], []);

    [Fact]
    public async Task A_description_without_a_repository_reads_nothing()
    {
        var source = new Source(Task.FromResult(Snapshot()));
        var catalog = new InfrastructureCatalog(source, DescriptionScope.None, _time, _options);

        var report = await catalog.GetAsync(refresh: true, Ct);

        source.Calls.Should().Be(0);
        report.Index.InScope.Should().BeFalse();
    }

    [Fact]
    public async Task A_request_waits_a_little_for_the_reading_then_gets_what_was_read_last_and_the_screens_are_told_when_it_ends()
    {
        var gate = new TaskCompletionSource<InfrastructureSnapshot>();
        var source = new Source(gate.Task);
        var catalog = new InfrastructureCatalog(source, Scope, _time, _options);
        var changed = new TaskCompletionSource();
        catalog.Changed += () => changed.TrySetResult();

        var request = catalog.GetAsync(refresh: false, Ct);
        _time.Advance(TimeSpan.FromSeconds(8));
        var first = await request;

        first.Index.InScope.Should().BeFalse("the reading is not over: the table is served without the code, and does not wait");
        gate.SetResult(Snapshot());
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        catalog.Peek().Index.FileCount.Should().Be(1);
        (await catalog.GetAsync(refresh: false, Ct)).Index.InScope.Should().BeTrue();
        source.Calls.Should().Be(1, "a request that comes while it is being read does not start another reading");
    }

    [Fact]
    public async Task What_was_read_is_kept_and_a_refresh_does_not_read_again_before_the_shortest_interval()
    {
        var source = new Source(Task.FromResult(Snapshot()));
        var catalog = new InfrastructureCatalog(source, Scope, _time, _options);
        await catalog.GetAsync(refresh: false, Ct);

        _time.Advance(TimeSpan.FromSeconds(30));
        await catalog.GetAsync(refresh: true, Ct);
        source.Calls.Should().Be(1);

        _time.Advance(TimeSpan.FromSeconds(40));
        await catalog.GetAsync(refresh: true, Ct);
        await source.Reading(2);
        source.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_reading_that_fails_says_so_keeps_the_last_good_index_and_is_not_tried_again_at_once()
    {
        var source = new Source(Task.FromResult(Snapshot()));
        var catalog = new InfrastructureCatalog(source, Scope, _time, _options);
        await catalog.GetAsync(refresh: false, Ct);
        _time.Advance(TimeSpan.FromMinutes(6));
        source.Next = Task.FromException<InfrastructureSnapshot>(new HttpRequestException("down"));

        await catalog.GetAsync(refresh: false, Ct);
        await source.Reading(2);
        var failed = catalog.Peek();
        await catalog.GetAsync(refresh: false, Ct);

        failed.Stale.Should().BeTrue();
        failed.Error.Should().Be(nameof(HttpRequestException));
        failed.Index.FileCount.Should().Be(1, "the last good index stays");
        source.Calls.Should().Be(2, "a failed reading waits for the shortest interval");
    }

    private sealed class Source(Task<InfrastructureSnapshot> first) : IInfrastructureSource
    {
        private readonly List<TaskCompletionSource> _reads = [];
        private int _calls;

        public Task<InfrastructureSnapshot> Next { get; set; } = first;

        public int Calls => _calls;

        public Task<InfrastructureSnapshot> ReadAsync(IReadOnlyList<RepositoryScope> scope, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            lock (_reads)
            {
                foreach (var read in _reads.ToList())
                {
                    read.TrySetResult();
                }
            }
            return Next;
        }

        // Waits until the reading number n has started (a reading runs in the background)
        public async Task Reading(int n)
        {
            for (var i = 0; i < 200 && _calls < n; i++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }
    }
}
