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

    private static IEnumerable<CodeLocation> KustomizeTags(string repository, string path, string content)
    {
        var lines = content.Split('\n');
        string? name = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var flow = KustomizeFlow().Match(lines[i]);
            if (flow.Success)
            {
                if (ImageReference.Parse($"{flow.Groups["name"].Value}:{flow.Groups["tag"].Value}") is { } image)
                {
                    yield return new CodeLocation(repository, path, i + 1, VersionForm.KustomizeTag, image);
                }
                continue;
            }
            var named = KustomizeName().Match(lines[i]);
            if (named.Success)
            {
                name = named.Groups["name"].Value;
                continue;
            }
            var tag = KustomizeNewTag().Match(lines[i]);
            if (tag.Success && name is not null && ImageReference.Parse($"{name}:{tag.Groups["tag"].Value}") is { } block)
            {
                yield return new CodeLocation(repository, path, i + 1, VersionForm.KustomizeTag, block);
                name = null;
            }
        }
    }

    // In a Terraform file the image is a string given to name (docker_image) or image (a container); a reference through an expression has no version to read
    private static IEnumerable<CodeLocation> TerraformImages(string repository, string path, string content)
    {
        var lines = content.Split('\n');
        var inDockerImage = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (ResourceStart().Match(lines[i]) is { Success: true } start)
            {
                inDockerImage = start.Groups["type"].Value == "docker_image";
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

    [GeneratedRegex(@"name:\s*(?<name>[^,\s}]+)\s*,\s*newTag:\s*(?<tag>[^,\s}]+)", RegexOptions.None, 250)]
    private static partial Regex KustomizeFlow();

    [GeneratedRegex(@"^\s*-?\s*name:\s*(?<name>\S+)\s*$", RegexOptions.None, 250)]
    private static partial Regex KustomizeName();

    [GeneratedRegex(@"^\s*newTag:\s*(?<tag>[^\s#]+)", RegexOptions.None, 250)]
    private static partial Regex KustomizeNewTag();

    [GeneratedRegex(@"^\s*resource\s+""(?<type>[a-z_]+)""", RegexOptions.None, 250)]
    private static partial Regex ResourceStart();

    [GeneratedRegex(@"^\s*(?<attribute>name|image)\s*=\s*""(?<ref>[^""]+)""", RegexOptions.None, 250)]
    private static partial Regex TerraformString();

    [GeneratedRegex(@"^(?<key>[A-Za-z_][\w-]*):\s*(?<value>[^#]*)", RegexOptions.None, 250)]
    private static partial Regex VariableLine();
}
