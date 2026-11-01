namespace Orchestrarr.Core.Fleet;

/// <summary>Used when no source is configured: it reads nothing, and the status page says so.</summary>
public sealed class EmptyFleetSource : IFleetSource
{
    private readonly TimeProvider _time;

    public EmptyFleetSource(TimeProvider time)
    {
        _time = time;
    }

    public string Kind => "none";

    public Task<FleetSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new FleetSnapshot(_time.GetUtcNow(), [], []));
    }
}
