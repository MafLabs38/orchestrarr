using System.Text;
using System.Text.RegularExpressions;

namespace Orchestrarr.Core.Infrastructure;

/// <summary>A change to one line of a file, with the file as it would be and the change as a unified diff.</summary>
public sealed record VersionChange(string Path, string Before, string After, string NewContent, string Diff);

/// <summary>
/// The change of a version in the code, computed and not applied (ADR 0028): the line found by <see cref="VersionParsers"/> gets the new tag, nothing else of the file
/// moves. Orchestrarr holds no write power; the person, or later the Runner, applies it.
/// </summary>
public static partial class VersionPatch
{
    /// <summary>The change that writes <paramref name="newTag"/> where <paramref name="location"/> is; null when the line is not what was read, or is not ours to change.</summary>
    public static VersionChange? Replace(string content, CodeLocation location, string newTag)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(location);
        if (location.Managed is not null || string.IsNullOrWhiteSpace(newTag) || newTag.Any(char.IsWhiteSpace))
        {
            return null;
        }
        var lines = content.Split('\n');
        if (location.Line < 1 || location.Line > lines.Length)
        {
            return null;
        }
        var index = location.Line - 1;
        var original = lines[index];
        var changed = location.Form switch
        {
            VersionForm.KustomizeTag => ReplaceNewTag(original, newTag),
            VersionForm.Variable => ReplaceVariable(original, newTag),
            _ => ReplaceReference(original, location, newTag),
        };
        if (changed is null || changed == original)
        {
            return null;
        }
        lines[index] = changed;
        var newContent = string.Join('\n', lines);
        return new VersionChange(location.Path, original.TrimEnd('\r'), changed.TrimEnd('\r'), newContent, Diff(location.Path, lines, original, changed, index));
    }

    private static string? ReplaceNewTag(string line, string newTag)
    {
        var flow = FlowTag().Match(line);
        if (flow.Success)
        {
            return Splice(line, flow.Groups["tag"], newTag);
        }
        var block = BlockTag().Match(line);
        return block.Success ? Splice(line, block.Groups["tag"], newTag) : null;
    }

    // The reference as it is written on the line (registry and quotes kept): the name stays, the tag (or digest) becomes the new tag
    private static string? ReplaceReference(string line, CodeLocation location, string newTag)
    {
        var match = Quoted().Match(line);
        if (!match.Success)
        {
            return null;
        }
        var written = match.Groups["ref"];
        if (ImageReference.Parse(written.Value) is not { } parsed || parsed.Name != location.Image.Name)
        {
            return null;
        }
        var text = written.Value;
        var at = text.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            text = text[..at];
        }
        var colon = text.LastIndexOf(':');
        if (colon > text.LastIndexOf('/'))
        {
            text = text[..colon];
        }
        return Splice(line, written, $"{text}:{newTag}");
    }

    private static string Splice(string line, Group group, string replacement) => string.Concat(line.AsSpan(0, group.Index), replacement, line.AsSpan(group.Index + group.Length));

    // A value of a variable file: after the key and its colon, inside its quotes if it has some
    private static string? ReplaceVariable(string line, string newValue)
    {
        var match = VariableValue().Match(line);
        return match.Success ? Splice(line, match.Groups["value"], newValue) : null;
    }

    private static string Diff(string path, string[] lines, string original, string changed, int index)
    {
        const int context = 3;
        var from = Math.Max(0, index - context);
        var to = Math.Min(lines.Length - 1, index + context);
        var text = new StringBuilder();
        text.Append("--- a/").Append(path).Append('\n').Append("+++ b/").Append(path).Append('\n');
        var count = to - from + 1;
        text.Append("@@ -").Append(from + 1).Append(',').Append(count).Append(" +").Append(from + 1).Append(',').Append(count).Append(" @@\n");
        for (var i = from; i <= to; i++)
        {
            if (i == index)
            {
                text.Append('-').Append(original.TrimEnd('\r')).Append('\n').Append('+').Append(changed.TrimEnd('\r')).Append('\n');
            }
            else
            {
                text.Append(' ').Append(lines[i].TrimEnd('\r')).Append('\n');
            }
        }
        return text.ToString();
    }

    [GeneratedRegex(@"(?<=image:\s*|=\s*)(?:""(?<ref>[^""]*)""|'(?<ref>[^']*)'|(?<ref>[^\s#""']+))")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^\s*[A-Za-z_][\w-]*:\s*[""']?(?<value>[^""'#\s]+)")]
    private static partial Regex VariableValue();

    [GeneratedRegex(@"newTag:\s*(?<tag>[^,\s}#]+)")]
    private static partial Regex FlowTag();

    [GeneratedRegex(@"^\s*newTag:\s*(?<tag>[^\s#]+)")]
    private static partial Regex BlockTag();
}
