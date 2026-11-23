using Orchestrarr.Core.Forge;

namespace Orchestrarr.Core.Missions;

/// <summary>
/// What may be **integrated** when a worker agent solves a problem that has no pull request (ADR 0048). Inside the machine the agent is free: it installs, builds, tests and breaks what it needs, and no rule says how it works.
/// The frame is on what it hands over, because that is what reaches a repository and then production: never the pipelines, the files that carry secrets, the files that instruct an agent or the repository's own metadata. Whatever the
/// agent did with the tests and the dependencies is its own judgement, and the pipelines and the controller judge it afterwards. It is read by the runner that collects the files and by the control plane that opens the pull request:
/// the control plane's own reading is the one that counts.
/// </summary>
public static class ChangePolicy
{
    public const int MaxFiles = 60;
    public const int MaxFileBytes = 1_000_000;
    public const int MaxTotalBytes = 4_000_000;

    /// <summary>The paths that never leave the machine, whatever the problem: how the code is built and released by the forge, what carries a secret, what instructs an agent.</summary>
    public static readonly IReadOnlyList<string> ProtectedPaths =
    [
        ".github/**", ".gitlab-ci.yml", ".circleci/**", "**/Jenkinsfile*", "azure-pipelines.yml", "**/.travis.yml", "**/bitbucket-pipelines.yml",
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
                return $"the path '{file.Path}' is protected: a change never carries the pipelines, the files that hold a secret or the files that instruct an agent";
            }
            if (!CarriedStatuses.Contains(file.Status, StringComparer.Ordinal))
            {
                return $"'{file.Path}' is {file.Status}: a change is carried as added and modified files";
            }
            var size = file.Patch?.Length ?? 0;
            total += size;
            if (size > MaxFileBytes || total > MaxTotalBytes || (file.Patch is null && file.Changes > 0 && !IsGenerated(file.Path)))
            {
                return $"the file '{file.Path}' is too big, or is not text";
            }
        }
        return null;
    }

    /// <summary>A file that a tool writes (a lock file): its lines are not shown to the controller, and the forge may give no patch for it.</summary>
    public static bool IsGenerated(string path) => path.EndsWith("lock.json", StringComparison.Ordinal) || path.EndsWith(".lock", StringComparison.Ordinal) || path.EndsWith("lock.yaml", StringComparison.Ordinal);

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
        var lines = string.Join('\n', shown.SelectMany(file => file.Patch!.Split('\n').Where(line => line.StartsWith('+') || line.StartsWith('-')).Select(line => line.TrimEnd('\r'))));
        var cut = stat.Length > StatCharacters || lines.Length > LinesCharacters || change.Files.Count > MaxShownFiles;
        var statText = stat.Length > StatCharacters ? stat[..StatCharacters] : stat;
        var linesText = lines.Length > LinesCharacters ? lines[..LinesCharacters] : lines;
        var text = $"files: {statText}. Lines: {(linesText.Length == 0 ? "none that are not generated" : linesText)}";
        var generated = change.Files.Where(file => IsGenerated(file.Path)).Select(file => file.Path).ToList();
        return $"{text}{(generated.Count > 0 ? $". Generated files changed too (their lines are not shown): {string.Join(", ", generated)}" : string.Empty)}{(cut ? " [CUT: the rest is not shown]" : string.Empty)}";
    }
}
