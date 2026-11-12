using System.Text.RegularExpressions;

namespace Orchestrarr.Core.Infrastructure;

/// <summary>
/// Finds the versions written in a file of a repository of the description (ADR 0028). Deterministic and line by line: what the file says, nothing guessed. A kind that
/// is not known finds nothing.
/// </summary>
public static partial class VersionParsers
{
    private delegate IEnumerable<CodeLocation> Reader(string repository, string path, string content);

    // The kinds of files the description may name, and how each is read: the validation of the description and the reading are one list
    private static readonly Dictionary<string, Reader> Readers = new(StringComparer.Ordinal)
    {
        ["kubernetes"] = ImageLines,
        ["compose"] = ImageLines,
        ["kustomize"] = (repository, path, content) => [.. ImageLines(repository, path, content), .. KustomizeTags(repository, path, content)],
        ["terraform-docker"] = TerraformImages,
        // A file of variables (the version of K3s): read for the platforms of the description, it holds no image
        ["variables"] = (_, _, _) => [],
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

    /// <summary>The value of a key of a variable file (<c>k3s_version: "v1.36.5+k3s1"</c>) and the line it is on; null when there is none.</summary>
    public static (int Line, string Value)? Variable(string content, string key)
    {
        ArgumentNullException.ThrowIfNull(content);
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = VariableLine().Match(lines[i]);
            if (match.Success && match.Groups["key"].Value == key)
            {
                return (i + 1, match.Groups["value"].Value.Trim().Trim('"', '\''));
            }
        }
        return null;
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
            var managed = lines[i].Contains("$imagepolicy", StringComparison.Ordinal) ? "flux" : null;
            yield return new CodeLocation(repository, path, i + 1, VersionForm.ImageLine, image, managed);
        }
    }

    // An entry of the images of a kustomization: the image it names, the name it becomes (newName, the image that is deployed) and its newTag, in flow or in block style
    private static IEnumerable<CodeLocation> KustomizeTags(string repository, string path, string content)
    {
        var lines = content.Split('\n');
        string? name = null;
        string? newName = null;
        string? tag = null;
        var tagLine = 0;
        var flux = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var flow = KustomizeFlow().Match(lines[i]);
            if (flow.Success)
            {
                if (Entry(name, newName, tag) is { } pending)
                {
                    yield return new CodeLocation(repository, path, tagLine, VersionForm.KustomizeTag, pending, flux ? "flux" : null);
                }
                (name, newName, tag) = (null, null, null);
                var fields = FlowFields().Matches(flow.Groups["body"].Value).ToDictionary(match => match.Groups["key"].Value, match => match.Groups["value"].Value, StringComparer.Ordinal);
                if (Entry(fields.GetValueOrDefault("name"), fields.GetValueOrDefault("newName"), fields.GetValueOrDefault("newTag")) is { } image)
                {
                    yield return new CodeLocation(repository, path, i + 1, VersionForm.KustomizeTag, image, lines[i].Contains("$imagepolicy", StringComparison.Ordinal) ? "flux" : null);
                }
                continue;
            }
            var named = KustomizeName().Match(lines[i]);
            if (named.Success)
            {
                if (Entry(name, newName, tag) is { } pending)
                {
                    yield return new CodeLocation(repository, path, tagLine, VersionForm.KustomizeTag, pending, flux ? "flux" : null);
                }
                (name, newName, tag, flux) = (named.Groups["name"].Value, null, null, false);
                continue;
            }
            if (KustomizeNewName().Match(lines[i]) is { Success: true } renamed)
            {
                newName = renamed.Groups["name"].Value;
            }
            else if (KustomizeNewTag().Match(lines[i]) is { Success: true } tagged)
            {
                (tag, tagLine) = (tagged.Groups["tag"].Value, i + 1);
                // Flux rewrites a line that carries its marker: the line is its, not ours
                flux = lines[i].Contains("$imagepolicy", StringComparison.Ordinal);
            }
        }
        if (Entry(name, newName, tag) is { } last)
        {
            yield return new CodeLocation(repository, path, tagLine, VersionForm.KustomizeTag, last, flux ? "flux" : null);
        }
    }

    // The image that is deployed: the new name when there is one, else the name; with the tag
    private static ImageReference? Entry(string? name, string? newName, string? tag) =>
        tag is null || (newName ?? name) is not { } deployed ? null : ImageReference.Parse($"{deployed}:{tag}");

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

    [GeneratedRegex(@"^(?<key>[A-Za-z_][\w-]*):\s*(?<value>[^#]*)", RegexOptions.None, 250)]
    private static partial Regex VariableLine();
}
