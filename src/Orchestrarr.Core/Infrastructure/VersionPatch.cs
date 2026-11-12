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
        // A reference pinned by digest is a decision: the pin is never dropped for a tag that a registry can move. A tag is written to a file: only what the grammar of a tag allows goes in, so that nothing can change the structure of the line or pin another digest
        if (location.Managed is not null || location.Image.Digest is not null || !ImageReference.IsTag(newTag))
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
            _ => ReplaceReference(original, location, newTag),
        };
        if (changed is null || changed == original)
        {
            return null;
        }
        lines[index] = changed;
        var newContent = string.Join('\n', lines);
        return new VersionChange(location.Path, original.TrimEnd('\r'), changed.TrimEnd('\r'), newContent, Diff(location.Path, location.Line, original, changed));
    }

    private static string? ReplaceNewTag(string line, string newTag)
    {
        var flow = FlowTag().Match(line);
        if (flow.Success)
        {
            return Splice(line, flow.Groups["tag"], Written(flow.Groups["tag"].Value, newTag));
        }
        var block = BlockTag().Match(line);
        return block.Success ? Splice(line, block.Groups["tag"], Written(block.Groups["tag"].Value, newTag)) : null;
    }

    // The tag as YAML must read it: inside the quotes the old one had, and quoted when it was bare and YAML would not read the new one as text ("1.10" is a float, 15 a number)
    private static string Written(string old, string newTag)
    {
        if (old is { Length: >= 2 } && (old[0] == '"' || old[0] == '\'') && old[^1] == old[0])
        {
            return $"{old[0]}{newTag}{old[0]}";
        }
        return NotText().IsMatch(newTag) ? $"\"{newTag}\"" : newTag;
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

    // The one line that changes, without the lines around it: they are the file of a private repository and may hold what the file name does not say
    private static string Diff(string path, int line, string original, string changed) =>
        $"--- a/{path}\n+++ b/{path}\n@@ -{line},1 +{line},1 @@\n-{original.TrimEnd('\r')}\n+{changed.TrimEnd('\r')}\n";

    [GeneratedRegex(@"(?<=image:\s*|=\s*)(?:""(?<ref>[^""]*)""|'(?<ref>[^']*)'|(?<ref>[^\s#""']+))", RegexOptions.None, 250)]
    private static partial Regex Quoted();

    // What a YAML reader takes for a number or a word, not for a text
    [GeneratedRegex(@"^(?:[-+]?(?:\d[\d_]*(?:\.[\d_]*)?|\.\d[\d_]*)(?:[eE][-+]?\d+)?|0[xob][0-9a-fA-F_]+|(?i:true|false|yes|no|on|off|null|y|n|~))$", RegexOptions.None, 250)]
    private static partial Regex NotText();

    [GeneratedRegex(@"newTag:\s*(?<tag>[^,\s}#]+)", RegexOptions.None, 250)]
    private static partial Regex FlowTag();

    [GeneratedRegex(@"^\s*newTag:\s*(?<tag>[^\s#]+)", RegexOptions.None, 250)]
    private static partial Regex BlockTag();
}
