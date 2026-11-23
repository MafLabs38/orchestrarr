namespace Orchestrarr.Core.Missions;

/// <summary>
/// What may **leave** the staging machine when a worker agent solves a problem that has no pull request (ADR 0048). Inside the machine the agent is free: it installs, builds, tests and breaks what it needs, and no rule says how it works.
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
        ".git/**", "**/.git/**", "**/.gitmodules",
        "**/.claude/**", "**/CLAUDE.md", "**/CLAUDE.local.md", "**/AGENTS.md", "**/.cursorrules", "**/.cursor/**", "**/.mcp.json",
        "**/.env", "**/.env.*", "**/*.pem", "**/*.key", "**/*.p12", "**/*.pfx", "**/id_rsa*", "**/id_ed25519*", "**/*.enc.yaml", "**/*.enc.yml", "**/*.enc.json", "**/secrets/**", "**/credentials*",
    ];

    /// <summary>What a build or an install leaves in the folder and is not part of the change: never carried, and never a reason to refuse.</summary>
    public static readonly IReadOnlyList<string> Artifacts =
    [
        "**/node_modules/**", "**/.venv/**", "**/venv/**", "**/__pycache__/**", "**/*.pyc", "**/dist/**", "**/build/**", "**/target/**", "**/.cache/**", "**/.npm/**", "**/.pytest_cache/**",
        "**/coverage/**", "**/.nyc_output/**", "**/*.log", "**/.DS_Store", "**/bin/**", "**/obj/**", "**/.gradle/**", "**/.next/**", "**/.parcel-cache/**", "**/.tox/**", "**/.mypy_cache/**",
    ];

    public static bool IsArtifact(string path) => PathPatterns.AnyMatches(Artifacts, [path]);

    /// <summary>Whether the path may not leave the machine: the protected paths, and the floor of the frame that no mission lifts.</summary>
    public static bool IsProtected(string path) => PathPatterns.AnyMatches(ProtectedPaths, [path]) || PathPatterns.AnyMatches(ForbiddenPathsDefaults.Floor, [path]);

    /// <summary>Why a set of files cannot be carried into a pull request, or null when it can: how many, how big, where (this policy and the paths that the frame of the mission forbids), and whether it is text.</summary>
    public static string? Refusal(IReadOnlyList<PatchedFile> files, IEnumerable<string>? forbidden = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count is 0 or > MaxFiles)
        {
            return $"a change carries between 1 and {MaxFiles} files";
        }
        if (files.Any(file => file?.Path is null || file.Content is null))
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
            var size = System.Text.Encoding.UTF8.GetByteCount(file.Content);
            total += size;
            if (size > MaxFileBytes || total > MaxTotalBytes || file.Content.Contains('\0', StringComparison.Ordinal))
            {
                return $"the file '{file.Path}' is too big, or is not text";
            }
        }
        return null;
    }
}
