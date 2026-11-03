using System.Text.Json;
using Orchestrarr.Core.Fleet;

namespace Orchestrarr.Providers.Portarr;

/// <summary>Reads a file with the shape of Portarr's maintenance report. For a demo, tests and working offline.</summary>
public sealed class SampleFleetSource : IFleetSource
{
    private readonly string _path;
    private readonly TimeProvider _time;

    public SampleFleetSource(string path, TimeProvider time)
    {
        _path = path;
        _time = time;
    }

    public string Kind => FleetSourceKinds.Sample;

    public bool Configured => true;

    public async Task<FleetSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(_path);
        var report = await JsonSerializer.DeserializeAsync<PortarrMaintenance>(stream, PortarrFleetMapper.JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"The sample file {_path} is empty.");
        return PortarrFleetMapper.Map(report, _time.GetUtcNow());
    }
}
