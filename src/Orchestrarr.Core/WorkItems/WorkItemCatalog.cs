using Microsoft.Extensions.Logging;
using Orchestrarr.Core.Fleet;

namespace Orchestrarr.Core.WorkItems;

/// <summary>Settings of the catalog (<c>WorkItems</c> section).</summary>
public sealed class WorkItemCatalogOptions
{
    public const string SectionName = "WorkItems";

    /// <summary>How long a read, good or failed, is kept before the source is read again.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The shortest time between two reads when the operator asks for a refresh: it protects the source.</summary>
    public TimeSpan MinRefreshInterval { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The work items as of the last read of the source. <c>Error</c>: why the last read failed. <c>Stale</c>: it failed and the items come
/// from an earlier read.
/// </summary>
public sealed record WorkItemReport(DateTimeOffset? ReadAt, string SourceKind, IReadOnlyList<WorkItem> Items, SourceError? Error, bool Stale);

/// <summary>
/// Reads the source of findings and builds the work items. A failed read is kept like a good one for the cache duration, so that a source
/// that is down is not asked again by every request.
/// </summary>
public sealed class WorkItemCatalog
{
    private readonly IFleetSource _source;
    private readonly IPowerPolicy _powers;
    private readonly TimeProvider _time;
    private readonly WorkItemCatalogOptions _options;
    private readonly ILogger<WorkItemCatalog> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private WorkItemReport? _last;
    private DateTimeOffset _lastAttempt;

    public WorkItemCatalog(IFleetSource source, IPowerPolicy powers, TimeProvider time, WorkItemCatalogOptions options, ILogger<WorkItemCatalog> logger)
    {
        _source = source;
        _powers = powers;
        _time = time;
        _options = options;
        _logger = logger;
    }

    public string SourceKind => _source.Kind;

    public bool SourceConfigured => _source.Configured;

    public async Task<WorkItemReport> GetAsync(bool refresh, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            var keep = refresh ? _options.MinRefreshInterval : _options.CacheDuration;
            if (_last is not null && now - _lastAttempt < keep)
            {
                return _last;
            }

            _lastAttempt = now;
            try
            {
                var fleet = await _source.ReadAsync(cancellationToken);
                _last = new WorkItemReport(fleet.ReadAt, _source.Kind, WorkItemBuilder.Build(fleet, _powers), Error: null, Stale: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Reading the {Kind} source failed", _source.Kind);
                var error = SourceErrors.From(ex);

                // Keep what was read before: a source that is down must not empty the screen
                _last = _last is null
                    ? new WorkItemReport(null, _source.Kind, [], error, Stale: false)
                    : _last with { Error = error, Stale = true };
            }

            return _last;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<WorkItem?> FindAsync(string id, CancellationToken cancellationToken)
    {
        var report = await GetAsync(refresh: false, cancellationToken);
        return report.Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
