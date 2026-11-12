namespace Orchestrarr.Core.Infrastructure;

/// <summary>
/// An image reference told apart: where the image is (<c>Name</c>, without the registry of Docker Hub, lower case), and which version of it (<c>Tag</c>, or the
/// <c>Digest</c>). Two references name the same image when their names are the same (ADR 0028).
/// </summary>
public sealed record ImageReference(string Name, string? Tag, string? Digest)
{
    private static readonly string[] HubPrefixes = ["docker.io/library/", "index.docker.io/library/", "registry-1.docker.io/library/", "docker.io/", "index.docker.io/", "registry-1.docker.io/", "library/"];

    /// <summary>Reads a reference as it is written in a manifest, a compose file or a Terraform file. Null when it is not one (empty, a variable, a template).</summary>
    public static ImageReference? Parse(string? text)
    {
        var value = text?.Trim().Trim('"', '\'');
        if (string.IsNullOrEmpty(value) || value.Contains("${", StringComparison.Ordinal) || value.Contains("{{", StringComparison.Ordinal) || value.Contains(' ', StringComparison.Ordinal))
        {
            return null;
        }
        string? digest = null;
        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            digest = value[(at + 1)..];
            value = value[..at];
        }
        string? tag = null;
        var colon = value.LastIndexOf(':');
        // A colon before the last slash is the port of a registry, not a tag
        if (colon > value.LastIndexOf('/'))
        {
            tag = value[(colon + 1)..];
            value = value[..colon];
        }
        value = value.ToLowerInvariant();
        var prefix = HubPrefixes.FirstOrDefault(candidate => value.StartsWith(candidate, StringComparison.Ordinal));
        if (prefix is not null)
        {
            value = value[prefix.Length..];
        }
        if (value.Length == 0)
        {
            return null;
        }
        return new ImageReference(value, string.IsNullOrEmpty(tag) ? null : tag, string.IsNullOrEmpty(digest) ? null : digest);
    }

    /// <summary>No tag, <c>latest</c>, or only a digest: the version is not told.</summary>
    public bool IsFloating => Tag is null or "latest";
}
