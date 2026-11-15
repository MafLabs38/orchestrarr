using System.Text;
using System.Text.RegularExpressions;

namespace Orchestrarr.Core.Missions;

/// <summary>
/// The forbidden paths of a frame (<c>.github/**</c>, <c>**/*.enc.*</c>): <c>**</c> crosses folders, <c>*</c> and <c>?</c> stay inside one. The control plane matches the changed paths against the frame of the mission itself, so that
/// the check does not depend on a list that a source of facts keeps (an operator can empty a setting, the certificate's floor still applies).
/// </summary>
public static class PathPatterns
{
    public static bool Matches(string pattern, string path)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/').TrimStart('/');
        return Regex.IsMatch(normalized, ToRegex(pattern), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }

    /// <summary>Whether any path matches any pattern.</summary>
    public static bool AnyMatches(IEnumerable<string> patterns, IEnumerable<string> paths)
    {
        var list = patterns.ToList();
        return paths.Any(path => list.Any(pattern => Matches(pattern, path)));
    }

    private static string ToRegex(string pattern)
    {
        var regex = new StringBuilder("^");
        var position = 0;
        while (position < pattern.Length)
        {
            var current = pattern[position];
            var doubled = current == '*' && position + 1 < pattern.Length && pattern[position + 1] == '*';
            if (doubled && position + 2 < pattern.Length && pattern[position + 2] == '/')
            {
                // "**/" is any number of folders, including none
                regex.Append("(?:.*/)?");
                position += 3;
            }
            else if (doubled)
            {
                // a trailing "**" is anything below
                regex.Append(".*");
                position += 2;
            }
            else
            {
                regex.Append(current switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(current.ToString()),
                });
                position++;
            }
        }
        return regex.Append('$').ToString();
    }
}
