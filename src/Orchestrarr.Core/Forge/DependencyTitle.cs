using System.Text.RegularExpressions;

namespace Orchestrarr.Core.Forge;

/// <summary>Reads what a dependency bot's pull request title says it updates: Dependabot's titles, and the usual ones of Renovate.</summary>
public static partial class DependencyTitle
{
    // The titles are text written by someone else: every expression has a time limit
    private const int TimeoutMilliseconds = 250;
    private const string Prefix = @"(?:[A-Za-z]+(?:\([^)]*\))?!?:\s*)?";

    // Dependabot: "chore(deps): Bump lodash from 4.17.4 to 4.17.21 in /app" (the prefix and the suffix are optional)
    [GeneratedRegex(@"^" + Prefix + @"[Bb]ump\s+(?<package>\S+)\s+from\s+(?<from>\S+)\s+to\s+(?<to>\S+)(?:\s+in\s+.*)?$", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex Single();

    // Dependabot, a group: "Bump the npm_and_yarn group across 1 directory with 3 updates"
    [GeneratedRegex(@"^" + Prefix + @"[Bb]ump\s+the\s+(?<group>\S+)\s+group\b.*$", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex Group();

    // Dependabot, several: "Bump qs and express", "Bump a, b and c"
    [GeneratedRegex(@"^" + Prefix + @"[Bb]ump\s+(?<packages>[^\s].*?)(?:\s+in\s+.*)?$", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex Several();

    // Renovate: "Update dependency lodash to v4.17.21", "chore(deps): update actions/checkout action to v4", "Update lodash"
    [GeneratedRegex(@"^" + Prefix + @"[Uu]pdate\s+(?:dependency\s+|dependencies\s+)?(?<package>\S+?)(?:\s+(?:action|docker tag|image|orb))?(?:\s+to\s+(?<to>\S+))?$", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex Renovate();

    [GeneratedRegex(@"^(?<major>\d+)(?:\.(?<minor>\d+))?(?:\.(?<patch>\d+))?", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex Version();

    public static IReadOnlyList<DependencyChange> Parse(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        try
        {
            return ParseTitle(title);
        }
        catch (RegexMatchTimeoutException)
        {
            // A title that takes too long to read is read as nothing: the screens show it as it is
            return [];
        }
    }

    private static IReadOnlyList<DependencyChange> ParseTitle(string title)
    {
        if (Single().Match(title) is { Success: true } single)
        {
            var from = single.Groups["from"].Value;
            var to = single.Groups["to"].Value;
            return [new DependencyChange(single.Groups["package"].Value, from, to, Kind(from, to))];
        }

        if (Group().Match(title) is { Success: true } group)
        {
            return [new DependencyChange($"{group.Groups["group"].Value} group", null, null, UpdateKind.Group)];
        }

        if (Renovate().Match(title) is { Success: true } renovate)
        {
            var to = renovate.Groups["to"];
            return [new DependencyChange(renovate.Groups["package"].Value, null, to.Success ? to.Value : null, UpdateKind.Unknown)];
        }

        if (Several().Match(title) is { Success: true } several)
        {
            return [.. several.Groups["packages"].Value.Split([", ", " and "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => new DependencyChange(name, null, null, UpdateKind.Unknown))];
        }

        return [];
    }

    /// <summary>Major, minor or patch from two versions; unknown when either is not a version (a tag such as <c>14-alpine</c> counts by its numbers).</summary>
    public static UpdateKind Kind(string from, string to)
    {
        if (IsDigest(from) && IsDigest(to))
        {
            return UpdateKind.Digest;
        }

        if (Numbers(from) is not { } before || Numbers(to) is not { } after)
        {
            return UpdateKind.Unknown;
        }

        if (before.Major != after.Major)
        {
            return UpdateKind.Major;
        }

        if (before.Minor != after.Minor)
        {
            return UpdateKind.Minor;
        }

        return before.Patch != after.Patch ? UpdateKind.Patch : UpdateKind.Unknown;
    }

    // A digest of an image: seven to sixty-four hexadecimal characters with at least one letter, often between backticks (a version always has a dot or is short)
    private static bool IsDigest(string text)
    {
        var value = text.Trim('`', '"', '\'');
        return value.Length is >= 7 and <= 64 && value.All(Uri.IsHexDigit) && value.Any(char.IsLetter);
    }

    private static (int Major, int Minor, int Patch)? Numbers(string version)
    {
        try
        {
            if (Version().Match(version.TrimStart('v', 'V')) is not { Success: true } match)
            {
                return null;
            }

            static int Part(Group group) => group.Success && int.TryParse(group.Value, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;
            return (Part(match.Groups["major"]), Part(match.Groups["minor"]), Part(match.Groups["patch"]));
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}
