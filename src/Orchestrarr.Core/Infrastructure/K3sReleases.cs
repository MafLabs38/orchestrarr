using System.Text.RegularExpressions;

namespace Orchestrarr.Core.Infrastructure;

/// <summary>A K3s version told apart: <c>v1.36.5+k3s1</c> is Kubernetes 1.36.5 and the first build of K3s on it.</summary>
public sealed partial record K3sVersion(int Major, int Minor, int Patch, int Build) : IComparable<K3sVersion>
{
    public static K3sVersion? Parse(string? text)
    {
        var match = Pattern().Match(text?.Trim() ?? string.Empty);
        return match.Success
            ? new K3sVersion(int.Parse(match.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(match.Groups["minor"].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(match.Groups["patch"].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(match.Groups["build"].Value, System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    public int CompareTo(K3sVersion? other)
    {
        if (other is null)
        {
            return 1;
        }
        var byKubernetes = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        return byKubernetes != 0 ? byKubernetes : Build.CompareTo(other.Build);
    }

    public override string ToString() => $"v{Major}.{Minor}.{Patch}+k3s{Build}";

    public static bool operator <(K3sVersion left, K3sVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(K3sVersion left, K3sVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(K3sVersion left, K3sVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(K3sVersion left, K3sVersion right) => left.CompareTo(right) >= 0;

    [GeneratedRegex(@"^v(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)\+k3s(?<build>\d+)$", RegexOptions.None, 250)]
    private static partial Regex Pattern();
}

/// <summary>The newest K3s of each minor version of Kubernetes, as the release channels of K3s say, and the one they call stable.</summary>
public sealed record K3sReleases(K3sVersion Stable, IReadOnlyDictionary<int, K3sVersion> LatestByMinor);

/// <summary>Where the releases of K3s are read from. Null when they cannot be read: the table then proposes nothing about K3s.</summary>
public interface IK3sReleases
{
    Task<K3sReleases?> GetAsync(CancellationToken cancellationToken);

    /// <summary>What was read last, without reading anything: null before the first read, or when the last one failed and none was kept.</summary>
    K3sReleases? Peek();
}

public sealed class NoK3sReleases : IK3sReleases
{
    public Task<K3sReleases?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<K3sReleases?>(null);

    public K3sReleases? Peek() => null;
}
