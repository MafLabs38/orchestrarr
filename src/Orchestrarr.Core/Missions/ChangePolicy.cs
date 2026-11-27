using Orchestrarr.Core.Forge;

namespace Orchestrarr.Core.Missions;

/// <summary>
/// What may be **integrated** when a worker agent solves a problem that has no pull request (ADR 0048). Inside the machine the agent is free: it installs, builds, tests and breaks what it needs, and no rule says how it works.
/// The frame is on what it hands over, because that is what reaches a repository and then production: never the files that carry secrets, the files that instruct an agent or the repository's own metadata; the pipelines are maintenance like the rest (the maintainer, 2026-11-26, ADR 0055). Whatever the
/// agent did with the tests and the dependencies is its own judgement, and the pipelines and the controller judge it afterwards. It is read by the control plane that opens the pull request:
/// the control plane's own reading is the one that counts.
/// </summary>
public static partial class ChangePolicy
{
    public const int MaxFiles = 60;
    /// <summary>The bounds are on the patch that the forge gives (its characters), which is what the controller and the policy read.</summary>
    public const int MaxFilePatch = 1_000_000;
    public const int MaxTotalPatch = 4_000_000;

    /// <summary>The paths that never leave the machine, whatever the problem: the repository's own metadata, where the dependencies come from, what carries a secret, what instructs an agent.</summary>
    public static readonly IReadOnlyList<string> ProtectedPaths =
    [
        ".git/**", "**/.git/**", "**/.gitmodules", "**/.gitattributes",
        // Where the dependencies come from: a registry that a change points elsewhere is a change of what is installed, not of what is asked for
        "**/.npmrc", "**/.yarnrc", "**/.yarnrc.yml", "**/pip.conf", "**/.pypirc", "**/.netrc",
        "**/.claude/**", "**/CLAUDE.md", "**/CLAUDE.local.md", "**/AGENTS.md", "**/.cursorrules", "**/.cursor/**", "**/.mcp.json",
        "**/.env", "**/.env.*", "**/*.pem", "**/*.key", "**/*.p12", "**/*.pfx", "**/id_rsa*", "**/id_ed25519*", "**/*.enc.yaml", "**/*.enc.yml", "**/*.enc.json", "**/secrets/**", "**/credentials*",
    ];

    /// <summary>Whether the path may not leave the machine: the protected paths, and the floor of the frame that no mission lifts.</summary>
    public static bool IsProtected(string path) => PathPatterns.AnyMatches(ProtectedPaths, [path]) || PathPatterns.AnyMatches(ForbiddenPathsDefaults.Floor, [path]);

    /// <summary>The statuses of a file that a change may have: added or modified. A deletion or a rename is not a change that is carried (the removal of a dependency is a change to its manifest and the code that used it).</summary>
    private static readonly string[] CarriedStatuses = ["added", "modified", "changed"];

    /// <summary>
    /// Why what a branch holds cannot become a pull request, or null when it can. **The forge's own reading**, never a runner's: how many files, where (this policy and the paths that the frame of the mission forbids), what happened to each,
    /// how big, and whether it is text (the forge gives no patch for a binary file or a very large one: a generated file may have none, anything else without a patch is refused).
    /// </summary>
    public static string? Refusal(BranchChange change, IEnumerable<string>? forbidden = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Files.Count is 0 or > MaxFiles || !change.FilesComplete)
        {
            return $"a change carries between 1 and {MaxFiles} files";
        }
        if (change.BehindBy != 0)
        {
            return "the default branch moved since the change was made";
        }
        // What the controller reads is the only guard against a subtle line in a permitted file: a change that it could not read in full is refused, and the agent makes it smaller or in several steps
        var readable = change.Files.Where(file => !IsGenerated(file.Path)).ToList();
        if (readable.Count > MaxShownFiles || readable.Sum(file => LineText(file.Patch).Length) > LinesCharacters)
        {
            return $"the change is too large for the controller to read in full (at most {MaxShownFiles} files that a tool did not write and {LinesCharacters} characters of changed lines)";
        }
        long total = 0;
        foreach (var file in change.Files)
        {
            if (!RepairPolicy.IsPlainPath(file.Path))
            {
                return $"the path '{file.Path}' is not one that a change may carry";
            }
            if (forbidden is not null && PathPatterns.AnyMatches(forbidden, [file.Path]))
            {
                return $"the path '{file.Path}' is forbidden by the frame of the mission";
            }
            if (IsProtected(file.Path))
            {
                return $"the path '{file.Path}' is protected: a change never carries the files that hold a secret, the files that instruct an agent or where the dependencies come from";
            }
            // A pipeline that the frame lets this operation change is never weakened by it (ADR 0055): read on the lines that the change adds
            if (PipelineChanges.IsPipeline(file.Path) && PipelineChanges.Weakening(file.Path, AddedLines(file.Patch)) is { } weakened)
            {
                return weakened;
            }
            if (!CarriedStatuses.Contains(file.Status, StringComparer.Ordinal))
            {
                return $"'{file.Path}' is {file.Status}: a change is carried as added and modified files";
            }
            // A file with no patch is one that the forge could not show: a binary file, an empty one, or one that is too large. Nothing is integrated that nobody could read, a lock file included
            var size = file.Patch?.Length ?? 0;
            total += size;
            if (IsGenerated(file.Path) && file.Patch is { } lockPatch && ForeignSource(file.Path, lockPatch) is { } foreign)
            {
                return $"the lock file '{file.Path}' points at {foreign}, which is not a registry that this policy knows";
            }
            if (size > MaxFilePatch || total > MaxTotalPatch || file.Patch is null)
            {
                return $"the file '{file.Path}' is too big, or is not text";
            }
        }
        return null;
    }

    private static readonly string[] GeneratedNames = ["package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "Pipfile.lock", "poetry.lock", "Cargo.lock", "composer.lock", "Gemfile.lock", "go.sum"];

    /// <summary>The hosts that a lock file may fetch from: the registries of the ecosystems. A lock file that a change points elsewhere is a change of what is installed.</summary>
    private static readonly string[] Registries = ["registry.npmjs.org", "registry.yarnpkg.com", "pypi.org", "files.pythonhosted.org", "crates.io", "static.crates.io", "index.crates.io", "repo.packagist.org", "rubygems.org"];

    /// <summary>A file that a tool writes (a lock file), by its name: its lines are not shown to the controller, so the policy reads them itself.</summary>
    public static bool IsGenerated(string path) => GeneratedNames.Contains(path[(path.LastIndexOf('/') + 1)..], StringComparer.Ordinal);

    [System.Text.RegularExpressions.GeneratedRegex(@"[a-z][a-z0-9+.-]*://([^/\s""']+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, 250)]
    private static partial System.Text.RegularExpressions.Regex Address();

    // The host of the first address that the lines ADDED to a lock file name and that is not a registry, or null when they all are
    private static readonly string[] NpmFamily = ["package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml"];

    private static string? ForeignSource(string path, string patch)
    {
        // The lock files of npm and yarn say where a package comes from in a few keys (and carry funding and homepage links beside them); the others (Cargo, poetry, Pipfile, composer, go) are read for every address that they add
        var everything = !NpmFamily.Contains(path[(path.LastIndexOf('/') + 1)..], StringComparer.Ordinal);
        // Only the lines that say where a package comes from (a funding link or a homepage is not where it is fetched from)
        foreach (var line in patch.Split('\n').Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal) && (everything || line.Contains("resolved", StringComparison.Ordinal) || line.Contains("tarball", StringComparison.Ordinal) || line.Contains("resolution", StringComparison.Ordinal))))
        {
            foreach (System.Text.RegularExpressions.Match address in Address().Matches(line))
            {
                var host = address.Groups[1].Value.Split('@')[^1].Split(':')[0];
                if (!Registries.Contains(host, StringComparer.OrdinalIgnoreCase))
                {
                    return host;
                }
            }
        }
        return null;
    }

    // The changed lines of a patch (those that start with a plus or a minus), as the controller reads them
    private static IEnumerable<string> AddedLines(string? patch) =>
        (patch ?? string.Empty).Split('\n').Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)).Select(line => line[1..]);

    private static string LineText(string? patch) =>
        patch is null ? string.Empty : string.Join('\n', patch.Split('\n').Where(line => line.StartsWith('+') || line.StartsWith('-')).Select(line => line.TrimEnd('\r')));

    private const int StatCharacters = 3_000;
    private const int LinesCharacters = 8_000;
    private const int MaxShownFiles = 40;

    /// <summary>
    /// What the controller reads of a change (ADR 0048): the size of every file, then the changed lines of the files that are not generated, from the forge's patches. A cut is said. The lines are the worker's own text: the
    /// controller reads them as data.
    /// </summary>
    public static string Reading(BranchChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var stat = string.Join("; ", change.Files.Take(MaxShownFiles).Select(file => $"{file.Path} ({file.Status}, {file.Changes} lines)"));
        var shown = change.Files.Where(file => !IsGenerated(file.Path) && file.Patch is { Length: > 0 }).Take(MaxShownFiles).ToList();
        var lines = string.Join('\n', shown.Select(file => LineText(file.Patch)));
        var cut = stat.Length > StatCharacters || lines.Length > LinesCharacters || change.Files.Count > MaxShownFiles;
        var statText = stat.Length > StatCharacters ? stat[..StatCharacters] : stat;
        var linesText = lines.Length > LinesCharacters ? lines[..LinesCharacters] : lines;
        var text = $"files: {statText}. Lines: {(linesText.Length == 0 ? "none that are not generated" : linesText)}";
        var generated = change.Files.Where(file => IsGenerated(file.Path)).Select(file => file.Path).ToList();
        return $"{text}{(generated.Count > 0 ? $". Generated files changed too (their lines are not shown, the control plane checked that every address in them is a registry): {string.Join(", ", generated)}" : string.Empty)}{(cut ? " [CUT: the rest is not shown]" : string.Empty)}";
    }
}
