namespace Orchestrarr.Providers.Portarr;

/// <summary>Settings of the Portarr provider (<c>Portarr</c> section). Without a <see cref="BaseUrl"/>, this provider is not used.</summary>
public sealed class PortarrOptions
{
    public const string SectionName = "Portarr";

    /// <summary>Base address of a Portarr. Use https, or a private network: the key is sent in a header.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>
    /// A Portarr API key. Never put it in a file that is committed: use an environment variable (<c>Portarr__ApiKey</c>), a secret store
    /// (<c>dotnet user-secrets</c>) or the secrets of the platform.
    /// </summary>
    public string? ApiKey { get; set; }

    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>The largest report accepted: a source that answers with more is refused, not buffered.</summary>
    public long MaxResponseBytes { get; set; } = 10 * 1024 * 1024;
}
