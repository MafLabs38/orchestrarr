using System.Net.Http.Json;
using Orchestrarr.Core.Fleet;

namespace Orchestrarr.Providers.Portarr;

/// <summary>Reads the fleet from a Portarr over its REST API. Read-only: it never writes to Portarr (SPEC D-03).</summary>
public sealed class PortarrFleetSource : IFleetSource, IIconSource
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
        using var response = await SendAsync("api/v1/maintenance", cancellationToken);
        response.EnsureSuccessStatusCode();
        var report = await response.Content.ReadFromJsonAsync<PortarrMaintenance>(PortarrFleetMapper.JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Portarr returned an empty maintenance report.");
        return PortarrFleetMapper.Map(report, _time.GetUtcNow(), await ReadApplicationsAsync(cancellationToken));
    }

    // The names and icons of the applications: nice to have, never a reason to lose the report (an older Portarr, or one that does not answer, gives none)
    private async Task<IReadOnlyList<PortarrApp>> ReadApplicationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync("api/v1/apps", cancellationToken);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<List<PortarrApp>>(PortarrFleetMapper.JsonOptions, cancellationToken) ?? []
                : [];
        }
        catch (Exception failure) when (failure is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    private Task<HttpResponseMessage> SendAsync(string path, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (!string.IsNullOrEmpty(_apiKey))
        {
            request.Headers.Add(ApiKeyHeader, _apiKey);
        }
        return _http.SendAsync(request, cancellationToken);
    }

    private const int MaxIconBytes = 256 * 1024;
    private static readonly TimeSpan IconLifetime = TimeSpan.FromHours(1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Until, IconImage? Image)> _icons = new(StringComparer.Ordinal);

    /// <summary>The picture of an icon, served by Portarr: kept for an hour (a missing one too, but not a failure of Portarr), and only a small picture is accepted.</summary>
    public async Task<IconImage?> GetAsync(string name, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_icons.TryGetValue(name, out var kept) && kept.Until > now)
        {
            return kept.Image;
        }
        IconImage? image = null;
        try
        {
            using var response = await SendAsync($"api/v1/icons/{Uri.EscapeDataString(name)}", cancellationToken);
            var type = response.Content.Headers.ContentType?.MediaType;
            if (response.StatusCode != System.Net.HttpStatusCode.NotFound && !response.IsSuccessStatusCode)
            {
                // A refusal or a failure of Portarr is not "there is no icon": it is not kept, the next request asks again
                return null;
            }
            if (response.IsSuccessStatusCode && type is "image/svg+xml" or "image/png" or "image/webp" or "image/jpeg" or "image/gif")
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                image = bytes.Length is > 0 and <= MaxIconBytes ? new IconImage(bytes, type) : null;
            }
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Not kept: the next request tries again
            return null;
        }
        if (_icons.Count > 500)
        {
            _icons.Clear();
        }
        _icons[name] = (now + IconLifetime, image);
        return image;
    }
}
