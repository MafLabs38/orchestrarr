using System.Text.RegularExpressions;

namespace Orchestrarr.Core.Missions;

/// <summary>
/// Where a change makes the dependencies come from (ADR 0055): a maintenance raises a version from the registry of its ecosystem; it does not point a manifest at an address, a git repository or a local path, and a container file does not
/// pipe a downloaded script into a shell. Read on the lines that a change adds (the free agent) or on the whole file (a repair): one rule for both, so neither carries a dependency that no registry vouches for.
/// </summary>
public static partial class DependencySources
{
    // The keys of package.json that say where the project lives, not where a dependency comes from
    private static readonly string[] NpmMetadata = ["homepage", "url", "repository", "bugs", "funding", "author", "email", "web", "$schema", "description", "license"];

    /// <summary>Why these lines of this file fetch a dependency from outside a registry, or null when they do not (or when the file is not a manifest nor a container file).</summary>
    public static string? Foreign(string path, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(lines);
        var name = path[(path.LastIndexOf('/') + 1)..];
        var rule = RuleFor(name);
        if (rule is null)
        {
            return null;
        }
        var npm = name == "package.json";
        var line = lines.FirstOrDefault(candidate => rule.IsMatch(candidate) && !(npm && NpmMetadata.Any(key => candidate.TrimStart().StartsWith($"\"{key}\"", StringComparison.Ordinal))));
        return line is null ? null : $"'{path}' fetches something from outside a registry ({Shown(line)}): a maintenance takes its dependencies from the registry of its ecosystem";
    }

    private static Regex? RuleFor(string name) => name switch
    {
        "package.json" => Npm(),
        "pyproject.toml" or "Pipfile" or "setup.cfg" or "setup.py" => PythonProject(),
        "go.mod" => Go(),
        "Cargo.toml" => Cargo(),
        "Gemfile" => Bundler(),
        "composer.json" => Composer(),
        "Directory.Packages.props" or "Directory.Build.props" => NuGet(),
        _ when name.StartsWith("requirements", StringComparison.Ordinal) && name.EndsWith(".txt", StringComparison.Ordinal) => Pip(),
        _ when name.EndsWith(".csproj", StringComparison.Ordinal) => NuGet(),
        _ when name.StartsWith("Dockerfile", StringComparison.Ordinal) || name.StartsWith("Containerfile", StringComparison.Ordinal) || name.EndsWith(".Dockerfile", StringComparison.Ordinal) => PipedScript(),
        _ => null,
    };

    private static string Shown(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 120 ? trimmed[..120] : trimmed;
    }

    [GeneratedRegex(@"""\s*:\s*""(git\+|git:|github:|gitlab:|bitbucket:|https?:|file:|link:|portal:)", RegexOptions.IgnoreCase, 250)]
    private static partial Regex Npm();

    [GeneratedRegex(@"^\s*(-e\s|--editable\b|--index-url\b|--extra-index-url\b|-i\s|--find-links\b|-f\s|--trusted-host\b|git\+|https?://|file:)|@\s*(git\+|https?://|file:)", RegexOptions.IgnoreCase, 250)]
    private static partial Regex Pip();

    [GeneratedRegex(@"@\s*(git\+|https?://|file:)|^\s*(git|url)\s*=\s*""|\{[^}]*\b(git|path|url)\s*=", RegexOptions.IgnoreCase, 250)]
    private static partial Regex PythonProject();

    [GeneratedRegex(@"^\s*replace\b", RegexOptions.None, 250)]
    private static partial Regex Go();

    [GeneratedRegex(@"\{[^}]*\b(git|path)\s*=|^\s*git\s*=|^\s*\[patch\b", RegexOptions.None, 250)]
    private static partial Regex Cargo();

    [GeneratedRegex(@"\b(git|github|path):\s*['""]|:(git|github|path)\s*=>", RegexOptions.None, 250)]
    private static partial Regex Bundler();

    [GeneratedRegex(@"""type""\s*:\s*""(vcs|path|git|artifact)""", RegexOptions.IgnoreCase, 250)]
    private static partial Regex Composer();

    [GeneratedRegex(@"RestoreSources|RestoreAdditionalProjectSources", RegexOptions.None, 250)]
    private static partial Regex NuGet();

    // A script downloaded and run in one step, or a file added from an address
    [GeneratedRegex(@"\b(curl|wget)\b[^|#]*\|\s*(sudo\s+)?(ba|z|da|k)?sh\b|^\s*ADD\s+(--[a-z-]+=\S+\s+)*https?://", RegexOptions.IgnoreCase, 250)]
    private static partial Regex PipedScript();
}
