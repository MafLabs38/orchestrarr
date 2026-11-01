using Orchestrarr.Core.Fleet;

namespace Orchestrarr.Core.WorkItems;

/// <summary>The work items as of the last read of the source. <c>Stale</c>: the last read failed and the items come from an earlier one.</summary>
public sealed record WorkItemReport(DateTimeOffset? ReadAt, string SourceKind, IReadOnlyList<WorkItem> Items, string? Error, bool Stale);

/// <summary>Reads the source of findings (at most every 30 seconds unless asked) and builds the work items.</summary>
public sealed class WorkItemCatalog
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private readonly IFleetSource _source;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private WorkItemReport? _last;
    private DateTimeOffset _lastAttempt;

    public WorkItemCatalog(IFleetSource source, TimeProvider time)
    {
        _source = source;
        _time = time;
    }

    public string SourceKind => _source.Kind;

    public async Task<WorkItemReport> GetAsync(bool refresh, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            if (!refresh && _last is { Error: null } fresh && now - _lastAttempt < CacheDuration)
            {
                return fresh;
            }

            _lastAttempt = now;
            try
            {
                var fleet = await _source.ReadAsync(cancellationToken);
                _last = new WorkItemReport(fleet.ReadAt, _source.Kind, WorkItemBuilder.Build(fleet), Error: null, Stale: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Keep what was read before: a source that is down must not empty the screen
                _last = _last is null
                    ? new WorkItemReport(null, _source.Kind, [], ex.Message, Stale: false)
                    : _last with { Error = ex.Message, Stale = true };
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
