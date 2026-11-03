using System.Text.RegularExpressions;

namespace Orchestrarr.Core.Forge;

/// <summary>Reads what a dependency bot's pull request title says it updates.</summary>
public static partial class DependencyTitle
{
    // "chore(deps): Bump lodash from 4.17.4 to 4.17.21 in /app" (the prefix and the suffix are optional)
    [GeneratedRegex(@"^(?:[A-Za-z]+(?:\([^)]*\))?!?:\s*)?[Bb]ump\s+(?<package>\S+)\s+from\s+(?<from>\S+)\s+to\s+(?<to>\S+)(?:\s+in\s+.*)?$")]
    private static partial Regex Single();

    // "Bump qs and express", "Bump a, b and c"
    [GeneratedRegex(@"^(?:[A-Za-z]+(?:\([^)]*\))?!?:\s*)?[Bb]ump\s+(?<packages>[^\s].*?)(?:\s+in\s+.*)?$")]
    private static partial Regex Several();

    [GeneratedRegex(@"^(?<major>\d+)(?:\.(?<minor>\d+))?(?:\.(?<patch>\d+))?")]
    private static partial Regex Version();

    public static IReadOnlyList<DependencyChange> Parse(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        if (Single().Match(title) is { Success: true } single)
        {
            var from = single.Groups["from"].Value;
            var to = single.Groups["to"].Value;
            return [new DependencyChange(single.Groups["package"].Value, from, to, Kind(from, to))];
        }

        if (Several().Match(title) is { Success: true } several)
        {
            var text = several.Groups["packages"].Value;
            if (text.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
            {
                // "Bump the npm_and_yarn group with 3 updates": one change named after the group
                return [new DependencyChange(text, null, null, UpdateKind.Unknown)];
            }

            return [.. text.Split([", ", " and "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => new DependencyChange(name, null, null, UpdateKind.Unknown))];
        }

        return [];
    }

    /// <summary>Major, minor or patch from two versions; unknown when either is not a version (a tag such as <c>14-alpine</c> counts by its numbers).</summary>
    public static UpdateKind Kind(string from, string to)
    {
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

    private static (int Major, int Minor, int Patch)? Numbers(string version)
    {
        if (Version().Match(version.TrimStart('v', 'V')) is not { Success: true } match)
        {
            return null;
        }

        static int Part(Group group) => group.Success ? int.Parse(group.Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        return (Part(match.Groups["major"]), Part(match.Groups["minor"]), Part(match.Groups["patch"]));
    }
}
