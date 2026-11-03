using System.Net.Http.Json;
using Orchestrarr.Core.Fleet;

namespace Orchestrarr.Providers.Portarr;

/// <summary>Reads the fleet from a Portarr over its REST API. Read-only: it never writes to Portarr (SPEC D-03).</summary>
public sealed class PortarrFleetSource : IFleetSource
{
    public const string ApiKeyHeader = "X-Api-Key";

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly TimeProvider _time;

    internal PortarrFleetSource(HttpClient http, string? apiKey, TimeProvider time)
    {
        _http = http;
        _apiKey = apiKey;
        _time = time;
    }

    public string Kind => FleetSourceKinds.Portarr;

    public bool Configured => true;

    /// <summary>
    /// Builds the source. Redirects are never followed: the key must go to the configured address only, and a redirect from a Portarr
    /// behind a sign-in page is reported as such. The connections are recycled so that a changed address is picked up.
    /// <paramref name="handler"/> replaces the network in tests.
    /// </summary>
    public static PortarrFleetSource Create(PortarrOptions options, TimeProvider time, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.BaseUrl is null)
        {
            throw new InvalidOperationException("Portarr:BaseUrl is not set.");
        }

        handler ??= new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        var http = new HttpClient(handler)
        {
            BaseAddress = options.BaseUrl,
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
            MaxResponseContentBufferSize = options.MaxResponseBytes,
        };
        return new PortarrFleetSource(http, options.ApiKey, time);
    }

    public async Task<FleetSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/maintenance");
        if (!string.IsNullOrEmpty(_apiKey))
        {
            request.Headers.Add(ApiKeyHeader, _apiKey);
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var report = await response.Content.ReadFromJsonAsync<PortarrMaintenance>(PortarrFleetMapper.JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Portarr returned an empty maintenance report.");
        return PortarrFleetMapper.Map(report, _time.GetUtcNow());
    }
}
