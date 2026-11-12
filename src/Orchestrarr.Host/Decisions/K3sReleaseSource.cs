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

    /// <summary>How long a request waits for a reading that is going on: after it the table is served with what was read last, and the screens are told when it ends.</summary>
    public int FirstWaitSeconds { get; set; } = 5;
}

/// <summary>
/// The newest K3s of each minor version, from the public release channels (ADR 0028). Read in the background and kept for hours; when the channels cannot be read, or say
/// something that is not what they document, the last good answer is kept. Before any answer nothing is known, which the table treats as "nothing to say" and never as
/// "up to date".
/// </summary>
public sealed class K3sReleaseSource : IK3sReleases, IDisposable
{
    private readonly HttpClient? _http;
    private readonly Uri? _url;
    private readonly TimeProvider _time;
    private readonly TimeSpan _keep;
    private readonly TimeSpan _firstWait;
    private readonly object _gate = new();
    private (DateTimeOffset At, K3sReleases? Releases)? _last;
    private Task? _reading;

    public K3sReleaseSource(PlatformOptions options, TimeProvider time, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _time = time;
        _keep = TimeSpan.FromMinutes(Math.Max(1, options.CacheMinutes));
        _firstWait = TimeSpan.FromSeconds(Math.Max(1, options.FirstWaitSeconds));
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

    public event Action? Changed;

    public K3sReleases? Peek()
    {
        lock (_gate)
        {
            return _last?.Releases;
        }
    }

    public async Task<K3sReleases?> GetAsync(CancellationToken cancellationToken)
    {
        if (_http is null || _url is null)
        {
            return null;
        }
        Task reading;
        lock (_gate)
        {
            if (_last is { } kept && _time.GetUtcNow() - kept.At < _keep)
            {
                return kept.Releases;
            }
            _reading ??= Task.Run(ReadAsync, CancellationToken.None);
            reading = _reading;
        }
        // The reading goes on in the background; a request waits for it a little and no longer
        await Task.WhenAny(reading, Task.Delay(_firstWait, _time, cancellationToken));
        return Peek();
    }

    private async Task ReadAsync()
    {
        try
        {
            using var response = await _http!.GetAsync(_url!);
            response.EnsureSuccessStatusCode();
            var releases = Parse(await response.Content.ReadAsStringAsync());
            lock (_gate)
            {
                // An answer that says nothing usable does not replace the last good one, and is tried again in a minute like a failure
                _last = releases is null ? (_time.GetUtcNow() - _keep + TimeSpan.FromMinutes(1), _last?.Releases) : (_time.GetUtcNow(), releases);
            }
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            // The last good answer stays; the next try is a minute away, not at every read of the table
            lock (_gate)
            {
                _last = (_time.GetUtcNow() - _keep + TimeSpan.FromMinutes(1), _last?.Releases);
            }
        }
        finally
        {
            lock (_gate)
            {
                _reading = null;
            }
            Changed?.Invoke();
        }
    }

    internal static K3sReleases? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        K3sVersion? stable = null;
        var byMinor = new Dictionary<int, K3sVersion>();
        foreach (var channel in data.EnumerateArray().Where(channel => channel.ValueKind == JsonValueKind.Object))
        {
            var id = Text(channel, "id");
            if (K3sVersion.Parse(Text(channel, "latest")) is not { } latest)
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

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public void Dispose() => _http?.Dispose();
}
