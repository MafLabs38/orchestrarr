using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.Forge;

namespace Orchestrarr.Core.WorkItems;

/// <summary>Settings of the catalog (<c>WorkItems</c> section).</summary>
public sealed class WorkItemCatalogOptions
{
    public const string SectionName = "WorkItems";

    /// <summary>How long a read, good or failed, is kept before the source is read again.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The shortest time between two reads when the operator asks for a refresh: it protects the source.</summary>
    public TimeSpan MinRefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A read answers at once; it waits at most this long for the pull requests, which are still being read when it is exceeded.</summary>
    public TimeSpan ForgeFirstWait { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>The time allowed to read the pull requests of the whole fleet; what is not read by then has timed out.</summary>
    public TimeSpan ForgeTimeBudget { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How many repositories are read from the forge at the same time: the rate limit is shared.</summary>
    public int ForgeParallelism { get; set; } = 4;
}

/// <summary>
/// The work items as of the last read of the source. <c>Error</c>: why the last read failed. <c>Stale</c>: it failed and the items come
/// from an earlier read.
/// </summary>
public sealed record WorkItemReport(DateTimeOffset? ReadAt, string SourceKind, IReadOnlyList<WorkItem> Items, SourceError? Error, bool Stale);

/// <summary>
/// Reads the source of findings and builds the work items. A failed read is kept like a good one for the cache duration, so that a source
/// that is down is not asked again by every request. The pull requests are read from the forge after the report is built, in the background,
/// never under the lock: a slow forge cannot hold up what Portarr already says.
/// </summary>
public sealed class WorkItemCatalog : IDisposable
{
    private readonly IFleetSource _source;
    private readonly IPullRequestSource _forge;
    private readonly IPowerPolicy _powers;
    private readonly TimeProvider _time;
    private readonly WorkItemCatalogOptions _options;
    private readonly ILogger<WorkItemCatalog> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, PullRequestsRead> _lastGood = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _state = new();
    private volatile WorkItemReport? _last;
    private DateTimeOffset _lastAttempt;
    private int _generation;
    private Task _enrichment = Task.CompletedTask;

    public WorkItemCatalog(IFleetSource source, IPullRequestSource forge, IPowerPolicy powers, TimeProvider time, WorkItemCatalogOptions options, ILogger<WorkItemCatalog> logger)
    {
        _source = source;
        _forge = forge;
        _powers = powers;
        _time = time;
        _options = options;
        _logger = logger;
    }

    public string SourceKind => _source.Kind;

    public bool SourceConfigured => _source.Configured;

    public string ForgeKind => _forge.Kind;

    public bool ForgeConfigured => _forge.Configured;

    /// <summary>Completes when the pull requests of the last read are all read or failed. For tests.</summary>
    internal Task EnrichmentIdle => _enrichment;

    /// <summary>What was read last, without reading anything: null before the first read. The status page uses it.</summary>
    public WorkItemReport? Peek() => _last;

    public async Task<WorkItemReport> GetAsync(bool refresh, CancellationToken cancellationToken)
    {
        Task enrichment;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            var keep = refresh ? _options.MinRefreshInterval : _options.CacheDuration;
            if (_last is { } kept && now - _lastAttempt < keep)
            {
                return kept;
            }

            _lastAttempt = now;
            try
            {
                var fleet = await _source.ReadAsync(cancellationToken);
                var items = WithForgeState(WorkItemBuilder.Build(fleet, _powers));
                lock (_state)
                {
                    _last = new WorkItemReport(fleet.ReadAt, _source.Kind, items, Error: null, Stale: false);
                    _generation++;
                    _enrichment = StartEnrichment(_generation, items, refresh);
                    enrichment = _enrichment;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Reading the {Kind} source failed", _source.Kind);
                var error = SourceErrors.From(ex);

                // Keep what was read before: a source that is down must not empty the screen
                lock (_state)
                {
                    _last = _last is null
                        ? new WorkItemReport(null, _source.Kind, [], error, Stale: false)
                        : _last with { Error = error, Stale = true };
                    return _last;
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        // A fast forge shows its pull requests at once; a slow one never holds the request
        if (!enrichment.IsCompleted)
        {
            await Task.WhenAny(enrichment, Task.Delay(_options.ForgeFirstWait, _time, cancellationToken));
        }

        return _last;
    }

    public async Task<WorkItem?> FindAsync(string id, CancellationToken cancellationToken)
    {
        var report = await GetAsync(refresh: false, cancellationToken);
        return report.Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _lock.Dispose();
    }

    private static bool WaitsForPullRequests(WorkItem item) =>
        item.RepositoryFullName is not null && item.Findings.Any(f => f.Kind == FindingKind.OpenDependencyPullRequests);

    // The state of the pull requests of each work item as soon as the report exists: not connected, what was last read, or pending
    private IReadOnlyList<WorkItem> WithForgeState(IReadOnlyList<WorkItem> items)
    {
        return [.. items.Select(item =>
        {
            if (!WaitsForPullRequests(item))
            {
                return item;
            }

            if (!_forge.Configured)
            {
                return item with { PullRequests = PullRequestsRead.NotConnected };
            }

            return item with { PullRequests = _lastGood.TryGetValue(item.RepositoryFullName!, out var previous) ? previous : PullRequestsRead.Pending };
        })];
    }

    private Task StartEnrichment(int generation, IReadOnlyList<WorkItem> items, bool refresh)
    {
        if (!_forge.Configured)
        {
            return Task.CompletedTask;
        }

        var repositories = items.Where(WaitsForPullRequests).Select(i => i.RepositoryFullName!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return repositories.Count == 0 ? Task.CompletedTask : Task.Run(() => EnrichAsync(generation, repositories, refresh), _shutdown.Token);
    }

    private async Task EnrichAsync(int generation, IReadOnlyList<string> repositories, bool refresh)
    {
        try
        {
            using var budget = new CancellationTokenSource(_options.ForgeTimeBudget, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, budget.Token);
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.ForgeParallelism), CancellationToken = linked.Token };
            await Parallel.ForEachAsync(repositories, parallel, async (repository, token) =>
            {
                Apply(generation, repository, await ReadAsync(repository, refresh, token));
            });
        }
        catch (OperationCanceledException exception) when (!_shutdown.IsCancellationRequested)
        {
            // The time budget is spent: what is still pending did not answer in time
            _logger.LogWarning(exception, "Reading the pull requests of the fleet from the {Kind} forge exceeded its time budget", _forge.Kind);
            ApplyTimeout(generation);
        }
        catch (OperationCanceledException)
        {
            // Shutting down
        }
    }

    private async Task<PullRequestsRead> ReadAsync(string repository, bool refresh, CancellationToken cancellationToken)
    {
        try
        {
            var read = PullRequestsRead.Of(await _forge.ListDependencyPullRequestsAsync(repository, refresh, cancellationToken));
            _lastGood[repository] = read;
            return read;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Reading the pull requests of {Repository} from the {Kind} forge failed", Sanitize(repository), _forge.Kind);
            _lastGood.TryGetValue(repository, out var previous);
            return PullRequestsRead.Failure(SourceErrors.From(ex), previous);
        }
    }

    private void Apply(int generation, string repository, PullRequestsRead read)
    {
        lock (_state)
        {
            if (_generation != generation || _last is null)
            {
                return;
            }

            _last = _last with
            {
                Items = [.. _last.Items.Select(item =>
                    WaitsForPullRequests(item) && string.Equals(item.RepositoryFullName, repository, StringComparison.OrdinalIgnoreCase) ? item with { PullRequests = read } : item)],
            };
        }
    }

    private void ApplyTimeout(int generation)
    {
        lock (_state)
        {
            if (_generation != generation || _last is null)
            {
                return;
            }

            _last = _last with
            {
                Items = [.. _last.Items.Select(item =>
                {
                    if (item.PullRequests.Status != PullRequestsStatus.Pending)
                    {
                        return item;
                    }

                    _lastGood.TryGetValue(item.RepositoryFullName!, out var previous);
                    return item with { PullRequests = PullRequestsRead.Failure(SourceError.Timeout, previous) };
                })],
            };
        }
    }

    // A repository name comes from another system: it goes into the log without anything that could forge a line
    private static string Sanitize(string value)
    {
        var clean = new string([.. value.Where(c => !char.IsControl(c))]);
        return clean.Length > 120 ? clean[..120] : clean;
    }
}
