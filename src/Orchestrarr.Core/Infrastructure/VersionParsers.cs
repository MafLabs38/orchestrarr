using System.Text.RegularExpressions;

namespace Orchestrarr.Core.Infrastructure;

/// <summary>
/// Finds the versions written in a file of a repository of the description (ADR 0028). Deterministic and line by line: what the file says, nothing guessed. A kind that
/// is not known finds nothing.
/// </summary>
public static partial class VersionParsers
{
    /// <summary>The automation that rewrites a line that carries its marker: Flux.</summary>
    public const string Flux = "flux";

    private const string FluxMarker = "$imagepolicy";

    /// <summary>Flux rewrites a line that carries its marker: the line is its, not ours.</summary>
    private static string? ManagedOf(string line) => line.Contains(FluxMarker, StringComparison.Ordinal) ? Flux : null;

    private delegate IEnumerable<CodeLocation> Reader(string repository, string path, string content);

    // The kinds of files the description may name, and how each is read: the validation of the description and the reading are one list
    private static readonly Dictionary<string, Reader> Readers = new(StringComparer.Ordinal)
    {
        ["kubernetes"] = ImageLines,
        ["compose"] = ImageLines,
        ["kustomize"] = (repository, path, content) => [.. ImageLines(repository, path, content), .. KustomizeTags(repository, path, content)],
        ["terraform-docker"] = TerraformImages,
    };

    public static IReadOnlyCollection<string> Kinds => Readers.Keys;

    /// <summary>The versions of the images written in this file; nothing for a kind that is not known.</summary>
    public static IReadOnlyList<CodeLocation> Find(string kind, string repository, string path, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!Readers.TryGetValue(kind, out var reader))
        {
            return [];
        }
        try
        {
            return [.. reader(repository, path, content)];
        }
        catch (RegexMatchTimeoutException)
        {
            // A file made to make a pattern slow is a file that is not read
            return [];
        }
    }

    private static IEnumerable<CodeLocation> ImageLines(string repository, string path, string content)
    {
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = ImageLine().Match(lines[i]);
            if (!match.Success || ImageReference.Parse(match.Groups["ref"].Value) is not { } image)
            {
                continue;
            }
            // Flux rewrites a line that carries its marker: the line is its, not ours
            var managed = ManagedOf(lines[i]);
            yield return new CodeLocation(repository, path, i + 1, VersionForm.ImageLine, image, managed);
        }
    }

    // An entry of the images of a kustomization: the image it names, the name it becomes (newName, the image that is deployed) and its newTag, in flow or in block style
    private static IEnumerable<CodeLocation> KustomizeTags(string repository, string path, string content)
    {
        var lines = content.Split('\n');
        var entry = new KustomizeEntry();
        for (var i = 0; i < lines.Length; i++)
        {
            if (KustomizeFlow().Match(lines[i]) is { Success: true } flow)
            {
                foreach (var pending in entry.Flush(repository, path))
                {
                    yield return pending;
                }
                var fields = FlowFields().Matches(flow.Groups["body"].Value).ToDictionary(match => match.Groups["key"].Value, match => match.Groups["value"].Value, StringComparer.Ordinal);
                var inline = new KustomizeEntry { Name = fields.GetValueOrDefault("name"), NewName = fields.GetValueOrDefault("newName"), Tag = fields.GetValueOrDefault("newTag"), TagLine = i + 1, Managed = ManagedOf(lines[i]) };
                foreach (var location in inline.Flush(repository, path))
                {
                    yield return location;
                }
            }
            else if (KustomizeName().Match(lines[i]) is { Success: true } named)
            {
                foreach (var pending in entry.Flush(repository, path))
                {
                    yield return pending;
                }
                entry = new KustomizeEntry { Name = named.Groups["name"].Value };
            }
            else if (KustomizeNewName().Match(lines[i]) is { Success: true } renamed)
            {
                entry.NewName = renamed.Groups["name"].Value;
            }
            else if (KustomizeNewTag().Match(lines[i]) is { Success: true } tagged)
            {
                // Flux rewrites a line that carries its marker: the line is its, not ours
                (entry.Tag, entry.TagLine, entry.Managed) = (tagged.Groups["tag"].Value, i + 1, ManagedOf(lines[i]));
            }
        }
        foreach (var last in entry.Flush(repository, path))
        {
            yield return last;
        }
    }

    // The entry being read: what it names, what it becomes and its tag, whatever the order of the keys
    private sealed class KustomizeEntry
    {
        public string? Name { get; set; }

        public string? NewName { get; set; }

        public string? Tag { get; set; }

        public int TagLine { get; set; }

        public string? Managed { get; set; }

        // The image that is deployed: the new name when there is one, else the name; with the tag. Nothing for an entry with no tag.
        public IEnumerable<CodeLocation> Flush(string repository, string path)
        {
            if (Tag is not null && (NewName ?? Name) is { } deployed && ImageReference.Parse($"{deployed}:{Tag}") is { } image)
            {
                yield return new CodeLocation(repository, path, TagLine, VersionForm.KustomizeTag, image, Managed);
            }
            (Name, NewName, Tag, TagLine, Managed) = (null, null, null, 0, null);
        }
    }

    // In a Terraform file the image is a string given to name (docker_image) or image (a container); a reference through an expression has no version to read
    private static IEnumerable<CodeLocation> TerraformImages(string repository, string path, string content)
    {
        var lines = content.Split('\n');
        var inDockerImage = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (BlockStart().Match(lines[i]) is { Success: true } start)
            {
                // Any block that starts closes the one before: a name in a data or a locals block is not an image
                inDockerImage = start.Groups["block"].Value == "resource" && start.Groups["type"].Value == "docker_image";
                continue;
            }
            var match = TerraformString().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }
            var attribute = match.Groups["attribute"].Value;
            if ((attribute == "name" && !inDockerImage) || ImageReference.Parse(match.Groups["ref"].Value) is not { } image)
            {
                continue;
            }
            yield return new CodeLocation(repository, path, i + 1, VersionForm.TerraformImage, image);
        }
    }

    [GeneratedRegex(@"^\s*-?\s*image:\s*(?<ref>""[^""]*""|'[^']*'|[^\s#]+)", RegexOptions.None, 250)]
    private static partial Regex ImageLine();

    [GeneratedRegex(@"\{(?<body>[^}]*\bnewTag:[^}]*)\}", RegexOptions.None, 250)]
    private static partial Regex KustomizeFlow();

    [GeneratedRegex(@"(?<key>name|newName|newTag):\s*(?<value>[^,\s}]+)", RegexOptions.None, 250)]
    private static partial Regex FlowFields();

    [GeneratedRegex(@"^\s*newName:\s*(?<name>[^\s#]+)", RegexOptions.None, 250)]
    private static partial Regex KustomizeNewName();

    [GeneratedRegex(@"^\s*-?\s*name:\s*(?<name>\S+)\s*$", RegexOptions.None, 250)]
    private static partial Regex KustomizeName();

    [GeneratedRegex(@"^\s*newTag:\s*(?<tag>[^\s#]+)", RegexOptions.None, 250)]
    private static partial Regex KustomizeNewTag();

    [GeneratedRegex(@"^(?<block>resource|data|locals|variable|output|provider|module|terraform|moved|import|check)\b(?:\s+""(?<type>[A-Za-z0-9_]+)"")?", RegexOptions.None, 250)]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"^\s*(?<attribute>name|image)\s*=\s*""(?<ref>[^""]+)""", RegexOptions.None, 250)]
    private static partial Regex TerraformString();
}
