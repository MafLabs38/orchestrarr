using System.Text.Json;
using Orchestrarr.Core.Infrastructure;

namespace Orchestrarr.Host.Decisions;

/// <summary>The <c>Platforms</c> section: where the releases of K3s are read from. No address: nothing is read and nothing is said about the versions of K3s.</summary>
public sealed class PlatformOptions
{
    public const string SectionName = "Platforms";

    /// <summary>
    /// The release channels of K3s, public and read without a credential (<c>https://update.k3s.io/v1-release/channels</c>). Must be https. Unset: off, so that a host
    /// that reaches nothing outside asks for nothing, and nothing is said about the versions of K3s.
    /// </summary>
    public string? K3sChannelsUrl { get; set; }

    public int CacheMinutes { get; set; } = 360;

    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>
/// The newest K3s of each minor version, from the public release channels (ADR 0028). Kept for hours; when the channels cannot be read the last good answer is kept, and
/// before any answer nothing is known, which the table treats as "nothing to say" and never as "up to date".
/// </summary>
public sealed class K3sReleaseSource : IK3sReleases, IDisposable
{
    private readonly HttpClient? _http;
    private readonly Uri? _url;
    private readonly TimeProvider _time;
    private readonly TimeSpan _keep;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (DateTimeOffset At, K3sReleases? Releases)? _last;

    public K3sReleaseSource(PlatformOptions options, TimeProvider time, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _time = time;
        _keep = TimeSpan.FromMinutes(Math.Max(1, options.CacheMinutes));
        if (string.IsNullOrWhiteSpace(options.K3sChannelsUrl))
        {
            return;
        }
        _url = Uri.TryCreate(options.K3sChannelsUrl, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
            ? url
            : throw new InvalidOperationException("Platforms:K3sChannelsUrl must be an https address.");
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)), MaxResponseContentBufferSize = 1024 * 1024 };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Orchestrarr");
    }

    public K3sReleases? Peek() => _last?.Releases;

    public async Task<K3sReleases?> GetAsync(CancellationToken cancellationToken)
    {
        if (_http is null || _url is null)
        {
            return null;
        }
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            if (_last is { } kept && now - kept.At < _keep)
            {
                return kept.Releases;
            }
            try
            {
                using var response = await _http.GetAsync(_url, cancellationToken);
                response.EnsureSuccessStatusCode();
                _last = (now, Parse(await response.Content.ReadAsStringAsync(cancellationToken)));
            }
            catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
            {
                // The last good answer stays; a failure is retried at the next call after a minute, not at every read of the table
                _last = (now - _keep + TimeSpan.FromMinutes(1), _last?.Releases);
            }
            return _last?.Releases;
        }
        finally
        {
            _lock.Release();
        }
    }

    internal static K3sReleases? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        K3sVersion? stable = null;
        var byMinor = new Dictionary<int, K3sVersion>();
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var channel in data.EnumerateArray())
        {
            var id = channel.TryGetProperty("id", out var name) ? name.GetString() : null;
            if (!channel.TryGetProperty("latest", out var newest) || K3sVersion.Parse(newest.GetString()) is not { } latest)
            {
                continue;
            }
            if (id == "stable")
            {
                stable = latest;
            }
            else if (id is not null && id.StartsWith('v') && id.Count(c => c == '.') == 1 && latest.Major == 1)
            {
                byMinor[latest.Minor] = latest;
            }
        }
        return stable is null ? null : new K3sReleases(stable, byMinor);
    }

    public void Dispose()
    {
        _http?.Dispose();
        _lock.Dispose();
    }
}
