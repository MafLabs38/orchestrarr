namespace Orchestrarr.Core.Infrastructure;

/// <summary>How long what was read from the repositories of the scope is kept (the forge's rate limit is shared).</summary>
public sealed class InfrastructureCatalogOptions
{
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan MinRefreshInterval { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>What the index says and how fresh it is: a read that failed keeps the last good index and says so.</summary>
public sealed record InfrastructureReport(InfrastructureIndex Index, DateTimeOffset? ReadAt, bool Stale, string? Error);

/// <summary>
/// The index of the code of the scope, read when asked and kept a few minutes (ADR 0028). It reads nothing when the description names no repository, and never
/// reads outside of it.
/// </summary>
public sealed class InfrastructureCatalog(IInfrastructureSource source, DescriptionScope scope, TimeProvider time, InfrastructureCatalogOptions options)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private InfrastructureReport _last = new(InfrastructureIndex.Empty, null, false, null);

    public DescriptionScope Scope => scope;

    /// <summary>What was read last, without reading anything.</summary>
    public InfrastructureReport Peek() => _last;

    public async Task<InfrastructureReport> GetAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (scope.Repositories.Count == 0)
        {
            return _last;
        }
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            var keep = refresh ? options.MinRefreshInterval : options.CacheDuration;
            if (_last.ReadAt is { } at && now - at < keep)
            {
                return _last;
            }
            try
            {
                var snapshot = await source.ReadAsync(scope.Repositories, cancellationToken);
                _last = new InfrastructureReport(InfrastructureIndex.Build(snapshot), now, false, null);
            }
            catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
            {
                // The last good index stays, marked stale: the table does not lose its places because the forge was down for a minute
                _last = _last with { Stale = _last.ReadAt is not null, Error = failure.GetType().Name, ReadAt = _last.ReadAt ?? now };
            }
            return _last;
        }
        finally
        {
            _lock.Release();
        }
    }
}
