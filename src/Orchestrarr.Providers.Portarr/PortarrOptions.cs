namespace Orchestrarr.Providers.Portarr;

/// <summary>Settings of the Portarr provider (<c>Portarr</c> section). With neither a URL nor a sample file, the provider reads nothing.</summary>
public sealed class PortarrOptions
{
    public const string SectionName = "Portarr";

    /// <summary>Base address of a Portarr, for example <c>http://portarr:8484</c>.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>The name of the environment variable that holds a Portarr API key. The key itself never goes in a file.</summary>
    public string ApiKeyEnv { get; set; } = "ORCHESTRARR_PORTARR_APIKEY";

    /// <summary>A JSON file with the shape of Portarr's maintenance report, read instead of a Portarr (demo, tests, offline).</summary>
    public string? SampleFile { get; set; }
}
