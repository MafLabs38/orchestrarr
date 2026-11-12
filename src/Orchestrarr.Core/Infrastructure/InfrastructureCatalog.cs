namespace Orchestrarr.Core.Infrastructure;

/// <summary>When and how long the code of the scope is read (the forge's rate limit is shared with the reader of the pull requests).</summary>
public sealed class InfrastructureCatalogOptions
{
    public const string SectionName = "Infrastructure";

    /// <summary>How long what was read is kept.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The shortest time between two readings when a refresh is asked, and before a failed reading is tried again.</summary>
    public TimeSpan MinRefreshInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a request waits for a reading that is going on: after it the table is served with what was read last, and the screens are told when the reading ends.</summary>
    public TimeSpan FirstWait { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>The longest a whole reading may take.</summary>
    public TimeSpan TimeBudget { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>What the index says and how fresh it is: a reading that failed keeps the last good index and says so.</summary>
public sealed record InfrastructureReport(InfrastructureIndex Index, DateTimeOffset? ReadAt, bool Stale, string? Error);

/// <summary>
/// The index of the code of the scope (ADR 0028). The forge is read in the background, never inside a request for longer than <c>FirstWait</c>: a request that comes
/// while it is being read gets what was read last, and <see cref="Changed"/> says when a new index is there. It reads nothing when the description names no repository,
/// and never reads outside of it.
/// </summary>
public sealed class InfrastructureCatalog(IInfrastructureSource source, DescriptionScope scope, TimeProvider time, InfrastructureCatalogOptions options)
{
    private readonly object _gate = new();
    private volatile InfrastructureReport _last = new(InfrastructureIndex.Empty, null, false, null);
    private Task? _reading;
    private DateTimeOffset? _notBefore;

    /// <summary>Raised when a reading ends with a new index or a new failure.</summary>
    public event Action? Changed;

    /// <summary>Raised with the exception when a reading fails, for the host's log: the screens only get its type.</summary>
    public event Action<Exception>? Failed;

    public DescriptionScope Scope => scope;

    /// <summary>What was read last, without reading anything.</summary>
    public InfrastructureReport Peek() => _last;

    public async Task<InfrastructureReport> GetAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (scope.Repositories.Count == 0)
        {
            return _last;
        }
        Task reading;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var keep = refresh ? options.MinRefreshInterval : options.CacheDuration;
            var fresh = _last.ReadAt is { } at && !_last.Stale && now - at < keep;
            var waiting = _notBefore is { } until && now < until;
            if (fresh || waiting)
            {
                return _last;
            }
            _reading ??= Task.Run(() => ReadAsync(CancellationToken.None), CancellationToken.None);
            reading = _reading;
        }
        // The reading goes on in the background whatever happens here; a request waits for it a little and no longer
        await Task.WhenAny(reading, Task.Delay(options.FirstWait, time, cancellationToken));
        return _last;
    }

    private void RaiseFailed(Exception failure)
    {
        try
        {
            Failed?.Invoke(failure);
        }
        catch (Exception subscriber) when (subscriber is not OutOfMemoryException)
        {
            // A subscriber that throws must not fault a reading nobody awaits
        }
    }

    // A subscriber that throws must not fault a reading nobody awaits
    private void Notify()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // The screens are told on the next reading
        }
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(options.TimeBudget);
            var snapshot = await source.ReadAsync(scope.Repositories, budget.Token);
            var now = time.GetUtcNow();
            _last = new InfrastructureReport(InfrastructureIndex.Build(snapshot, inScope: scope.Repositories.Count > 0), now, false, null);
            lock (_gate)
            {
                _notBefore = null;
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // The last good index stays, marked stale: the table does not lose its places because the forge was down for a minute. Not tried again before the shortest interval.
            var now = time.GetUtcNow();
            _last = _last with { Stale = _last.ReadAt is not null, Error = failure.GetType().Name };
            RaiseFailed(failure);
            lock (_gate)
            {
                _notBefore = now + options.MinRefreshInterval;
            }
        }
        finally
        {
            lock (_gate)
            {
                _reading = null;
            }
            Notify();
        }
    }
}
