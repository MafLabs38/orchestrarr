using System.Net.Http.Json;
using Orchestrarr.Core.Fleet;

namespace Orchestrarr.Providers.Portarr;

/// <summary>Reads the fleet from a Portarr over its REST API. Read-only: it never writes to Portarr (SPEC D-03).</summary>
public sealed class PortarrFleetSource : IFleetSource
{
    public const string HttpClientName = "portarr";
    public const string ApiKeyHeader = "X-Api-Key";

    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public PortarrFleetSource(HttpClient http, TimeProvider time)
    {
        _http = http;
        _time = time;
    }

    public string Kind => "portarr";

    public async Task<FleetSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var report = await _http.GetFromJsonAsync<PortarrMaintenance>("api/v1/maintenance", PortarrFleetMapper.JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Portarr returned an empty maintenance report.");
        return PortarrFleetMapper.Map(report, _time.GetUtcNow());
    }
}
