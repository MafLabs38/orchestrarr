using System.Text.RegularExpressions;

namespace Orchestrarr.Core.Missions;

/// <summary>
/// What a worker agent may change when it repairs a change that does not pass in staging, one policy read by the runner that runs the agent and by the control plane that opens the pull request (rule 14): the **source** of the application,
/// never what decides whether it passes (the tests and their configuration), what it depends on (manifests and lock files), how it is built and released (pipelines, images) or what instructs an agent. A repair that edits one of these has not
/// repaired anything. Matched with <see cref="PathPatterns"/> against the path from the root of the repository.
/// </summary>
public static partial class RepairPolicy
{
    public const int MaxFiles = 20;
    public const int MaxFileBytes = 200_000;
    public const int MaxTotalBytes = 500_000;

    public static readonly IReadOnlyList<string> ProtectedPaths =
    [
        // The dependencies are not among them (the maintainer, 2026-11-26, ADR 0055): a bump that needs a companion one (a library that requires a newer version of another) is repaired as a developer would, by raising it too
        // What decides whether it passes
        "test/**", "tests/**", "**/test/**", "**/tests/**", "**/__tests__/**", "**/*.test.*", "**/*.spec.*", "**/test_*.py", "**/*_test.py", "**/conftest.py", "**/pytest.ini", "**/tox.ini", "**/.mocharc*", "**/jest.config.*", "**/vitest.config.*",
        "**/.eslintrc*", "**/eslint.config.*", "**/tsconfig*.json", "**/Makefile",
        // What instructs an agent and the repository's metadata (the pipelines and the container files are maintenance like the rest: the maintainer, 2026-11-26, ADR 0055)
        ".git/**", "**/.git/**", "**/.claude/**", "**/CLAUDE.md", "**/CLAUDE.local.md", "**/.mcp.json", "**/.gitattributes", "**/.gitmodules",
        // The other ecosystems and forges: their tests, their pipelines, their runtimes and their package managers' settings. The list is a floor, not an inventory: a repair of a source that none of these names is still bounded by the limits and by the pipeline
        "**/*_test.go", "**/*.Tests/**", "**/*.Test/**", "**/*Tests.cs", "**/*Test.cs", "**/*Test.java", "**/*Tests.java", "spec/**", "**/spec/**", "**/*_spec.rb", "**/__snapshots__/**",
        "**/.nvmrc", "**/.node-version", "**/.python-version", "**/.tool-versions", "**/.yarnrc*", "**/.pnpmfile.cjs", "**/pnpm-workspace.yaml", "**/.npmrc", "**/pip.conf", "**/NuGet.config", "**/global.json", "**/dotnet-tools.json", "**/*.sln*",
        "**/jest.setup.*", "**/vitest.setup.*", "**/setupTests.*", "**/babel.config.*", "**/.babelrc*", "**/vite.config.*", "**/webpack.config.*", "**/rollup.config.*", "**/karma.conf.*", "**/playwright.config.*", "**/cypress/**", "**/e2e/**", "**/.husky/**", "**/.pre-commit-config.yaml", "**/*.snap", ".gitlab/**",
    ];

    /// <summary>Whether the agent may not change this path.</summary>
    public static bool IsProtected(string path) => PathPatterns.AnyMatches(ProtectedPaths, [path]) || PathPatterns.AnyMatches(ForbiddenPathsDefaults.Floor, [path]);

    /// <summary>A path the control plane and the runner both agree to carry: relative, plain characters, no spaces or quotes (a name that needs quoting in a script is refused, not escaped).</summary>
    public static bool IsPlainPath(string path) => path.Length is > 0 and <= 200 && PlainPath().IsMatch(path) && !path.StartsWith('/') && !path.Split('/').Any(segment => segment is "" or "." or "..");

    [GeneratedRegex(@"^[A-Za-z0-9_./@+-]+$", RegexOptions.None, 250)]
    private static partial Regex PlainPath();

    /// <summary>Why a set of files cannot be a repair, or null when it can: how many, how big, where (the policy, and the paths that the frame of the mission forbids), and whether it is text.</summary>
    /// <summary>Whether the guards read this file line by line (a pipeline, a manifest or a container file): its text at the head that is repaired is worth reading, so that only what the repair adds is judged.</summary>
    public static bool ReadsLines(string path) => PipelineChanges.IsPipeline(path) || DependencySources.Reads(path);

    /// <summary>
    /// Why a set of files cannot be a repair, or null when it can. <paramref name="before"/> holds the text of the files at the head that is repaired, when it was read: the guards of the pipelines and of the dependencies then judge
    /// the lines that the repair adds; a file that is new or was not read is judged whole.
    /// </summary>
    public static string? Refusal(IReadOnlyList<PatchedFile> files, IEnumerable<string>? forbidden = null, IReadOnlyDictionary<string, string>? before = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count is 0 or > MaxFiles)
        {
            return $"a repair changes between 1 and {MaxFiles} files";
        }
        if (files.Any(file => file?.Path is null))
        {
            return "a file has no path or no content";
        }
        if (files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != files.Count)
        {
            return "a file is named twice";
        }
        long total = 0;
        foreach (var file in files)
        {
            // What came over the wire can be null whatever the type says: a refusal, not an exception
            if (file is null || file.Path is null || file.Content is null)
            {
                return "a file has no path or no content";
            }
            if (!IsPlainPath(file.Path))
            {
                return $"the path '{file.Path}' is not one that a repair may carry";
            }
            // Beyond the policy, what the frame of the mission forbids (the options of the control plane and the paths that the maintainer's certificate adds)
            if (forbidden is not null && PathPatterns.AnyMatches(forbidden, [file.Path]))
            {
                return $"the path '{file.Path}' is forbidden by the frame of the mission";
            }
            if (IsProtected(file.Path))
            {
                return $"the path '{file.Path}' is protected: a repair does not change the tests, the registries of the package managers or the instructions of the repository";
            }
            // A pipeline that the frame lets this operation change is never weakened by a repair (ADR 0055): read on the lines it adds to the file at the repaired head, or on the whole file
            var added = Added(file, before);
            if (PipelineChanges.IsPipeline(file.Path) && (PipelineChanges.Weakening(file.Path, added) ?? Removal(file, before)) is { } weakened)
            {
                return weakened;
            }
            if (DependencySources.Foreign(file.Path, added) is { } foreignDependency)
            {
                return foreignDependency;
            }
            var size = System.Text.Encoding.UTF8.GetByteCount(file.Content);
            total += size;
            if (size > MaxFileBytes || total > MaxTotalBytes || file.Content.Contains('\0', StringComparison.Ordinal))
            {
                return $"the file '{file.Path}' is too big, or is not text";
            }
        }
        return null;
    }

    // The lines of the repaired file that the file at the repaired head does not hold (all of them when it is new or was not read)
    private static string[] Added(PatchedFile file, IReadOnlyDictionary<string, string>? before)
    {
        var lines = file.Content.Split('\n');
        if (before is null || !before.TryGetValue(file.Path, out var old))
        {
            return lines;
        }
        var held = old.Split('\n').Select(line => line.TrimEnd('\r')).ToHashSet(StringComparer.Ordinal);
        return [.. lines.Where(line => !held.Contains(line.TrimEnd('\r')))];
    }

    // The steps that a repair takes out of a pipeline file, against the file at the repaired head (nothing to compare with when it was not read)
    private static string? Removal(PatchedFile file, IReadOnlyDictionary<string, string>? before) =>
        before is not null && before.TryGetValue(file.Path, out var old) ? PipelineChanges.Removal(file.Path, old.Split('\n'), file.Content.Split('\n')) : null;
}
