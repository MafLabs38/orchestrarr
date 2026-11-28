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
    /// how big, and whether it is text (the forge gives no patch for a binary file or a very large one: a generated file may have none, anything else without a patch is refused). <paramref name="before"/> and <paramref name="after"/> are the
    /// texts of the workflows that the change touches, at the merge base and at the commit (ADR 0057): a workflow is then read as YAML and compared; a text that could not be read is null, and a workflow
    /// that is new has no text before.
    /// </summary>
    public static string? Refusal(BranchChange change, IEnumerable<string>? forbidden = null, IReadOnlyDictionary<string, string?>? before = null, IReadOnlyDictionary<string, string?>? after = null)
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
            // A pipeline that the frame lets this operation change is never weakened by it (ADR 0055): a workflow read as YAML against the file before it, any other pipeline on the lines that the change adds and removes
            if (SwappedBaseImage(file, Text(before, file.Path), Text(after, file.Path)) is { } swapped)
            {
                return swapped;
            }
            if (PipelineChanges.IsPipeline(file.Path) && PipelineGuard(file, before, after) is { } weakened)
            {
                return weakened;
            }
            // A manifest or a container file takes its dependencies from a registry (ADR 0055): read on the lines that the change adds
            if ((DependencySources.Foreign(file.Path, AddedLines(file.Patch)) ?? DependencySources.ScriptWeakening(file.Path, AddedLines(file.Patch))) is { } foreignDependency)
            {
                return foreignDependency;
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

    private static string? ForeignSource(string path, string patch) =>
        ForeignHost(path, patch.Split('\n').Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)).Select(line => line[1..]));

    /// <summary>The host of the first address that these lines, added to a lock file, fetch from and that is not a registry; null when they all are. Read for a free agent's change and for a repair.</summary>
    public static string? ForeignHost(string path, IEnumerable<string> addedLines)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(addedLines);
        // The lock files of npm and yarn say where a package comes from in a few keys (and carry funding and homepage links beside them); the others (Cargo, poetry, Pipfile, composer, go) are read for every address that they add
        var everything = !NpmFamily.Contains(path[(path.LastIndexOf('/') + 1)..], StringComparer.Ordinal);
        // Only the lines that say where a package comes from (a funding link or a homepage is not where it is fetched from)
        foreach (var line in addedLines.Where(line => everything || line.Contains("resolved", StringComparison.Ordinal) || line.Contains("tarball", StringComparison.Ordinal) || line.Contains("resolution", StringComparison.Ordinal)))
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

    /// <summary>Whether a path is a container file (a Dockerfile, a Containerfile), whose base images are compared before and after a change (ADR 0058).</summary>
    public static bool IsContainerFile(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Containerfile", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dockerfile", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Text(IReadOnlyDictionary<string, string?>? texts, string path) => texts is not null && texts.TryGetValue(path, out var text) ? text : null;

    // The image an application runs on (the final stage of a container file, followed through the stages it is built from) is raised to a newer tag of the same image, never replaced by another (ADR 0055, ADR 0058): a runtime
    // replaced is the maintainer's decision. The whole file is read before and after the change and the final FROM is resolved as Docker does (the ARGs declared before the first FROM, several on a line, continued lines,
    // `${BASE:-python:3.7}`); a file that could not be read whole, an ARG or a FROM that cannot be read, or a changed escape directive, is refused: the check fails closed. What a build runs on the way (a build stage, a
    // frontend, a `--from`, a RUN line) is the controller's to judge on the patch, like any line of the change
    internal static string? SwappedBaseImage(BranchFile file, string? before = null, string? after = null)
    {
        if (!IsContainerFile(file.Path))
        {
            return null;
        }
        // Without it, the build may read another container file of the change, which is then judged alone: removing one is the controller's and the maintainer's, never a free agent's
        if (file.Status == "removed")
        {
            return $"the change removes the container file '{file.Path}': the image the application runs on would then come from another file, which cannot be judged against it";
        }
        if (after is null || (before is null && file.Status != "added"))
        {
            return $"the container file '{file.Path}' could not be read whole before and after the change: the image it runs on cannot be judged";
        }
        if (before is not null && !string.Equals(EscapeOf(before), EscapeOf(after), StringComparison.Ordinal))
        {
            return $"the change alters the escape directive of '{file.Path}': the image it runs on cannot be judged";
        }
        var runtime = RuntimeOf(after);
        if (runtime.Contains('$'))
        {
            return $"'{file.Path}' would run on an image that cannot be told from the file ({runtime}): the image an application runs on is raised to a newer version of the same image, never replaced by another";
        }
        var previous = before is null ? runtime : RuntimeOf(before);
        return string.Equals(runtime, previous, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"the change makes '{file.Path}' run on '{runtime}' instead of '{previous}': the image an application runs on is raised to a newer version of the same image, never replaced by another (a runtime replaced is the maintainer's decision)";
    }

    // The image of the final stage, without tag or digest, the default registry and library left out, followed through the stages it is built from; an image that cannot be resolved keeps its `$`
    private static string RuntimeOf(string text)
    {
        var defaults = new Dictionary<string, string?>(StringComparer.Ordinal);
        var stages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var runtime = "$none";
        var seenFrom = false;
        var instructions = Instructions(text);
        // A heredoc holds lines that are not instructions (a FROM among them is not one): such a file is not judged here
        if (instructions.Exists(line => Heredoc().IsMatch(line)))
        {
            return "$heredoc";
        }
        foreach (var line in instructions)
        {
            var words = Words().Split(line).Where(word => word.Length > 0).ToList();
            if (!seenFrom && words[0].Equals("ARG", StringComparison.OrdinalIgnoreCase))
            {
                Declare([.. words.Skip(1)], defaults);
                continue;
            }
            if (!words[0].Equals("FROM", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            seenFrom = true;
            var rest = words.Skip(1).Where(word => !word.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (rest.Count == 0)
            {
                runtime = "$unreadable FROM";
                continue;
            }
            var image = Resolved(rest[0], defaults);
            runtime = image.Contains('$') ? image : Normalised(image);
            if (stages.TryGetValue(image, out var stage))
            {
                runtime = stage;
            }
            if (rest.Count >= 3 && rest[1].Equals("AS", StringComparison.OrdinalIgnoreCase))
            {
                stages[rest[2]] = runtime;
            }
        }
        return runtime;
    }

    // The ARG words of one instruction, applied in order (a value in quotes without spaces is that value); a word that is not `name` or `name=value` makes every name of the line unknown (the FROM that reads it is then unresolved)
    private static void Declare(List<string> words, Dictionary<string, string?> defaults)
    {
        var parsed = words.Select(word => ArgWord().Match(word)).ToList();
        var values = parsed.Select(match => match.Groups[2].Success ? Unquoted(match.Groups[3].Value) : null).ToList();
        if (parsed.Any(match => !match.Success) || values.Any(value => value is not null && (value.Contains('"') || value.Contains('\''))))
        {
            foreach (var name in words.Select(word => word.Split('=')[0]))
            {
                defaults[name] = "$unknown";
            }
            return;
        }
        for (var i = 0; i < parsed.Count; i++)
        {
            var name = parsed[i].Groups[1].Value;
            defaults[name] = values[i] ?? (defaults.ContainsKey(name) ? "$unknown" : null);
        }
    }

    private static string Unquoted(string value) =>
        value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0] ? value[1..^1] : value;

    // The instructions of a container file as Docker reads them: comment and blank lines left out first (also inside a continued instruction), then a line continued by `\` joined to the next
    private static List<string> Instructions(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(line => line.Trim() is { Length: > 0 } kept && !kept.StartsWith('#'));
        return [.. Continuation().Replace(string.Join('\n', lines), " ").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)];
    }

    private static string? EscapeOf(string text) => EscapeDirective().Match(text) is { Success: true } found ? found.Groups[1].Value : null;

    // ${X}, $X, ${X:-d} and ${X:+d} replaced by what the file says of X (its ARG default), or by the default written in place; a name whose value is unknown stays unresolved
    private static string Resolved(string word, Dictionary<string, string?> defaults) =>
        Variable().Replace(word, match =>
        {
            var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[4].Value;
            var known = defaults.GetValueOrDefault(name);
            return (match.Groups[2].Value, known) switch
            {
                (_, { } value) when value.Contains('$') => match.Value,
                (":-" or "-", null) => match.Groups[3].Value,
                (":-" or "-", { Length: 0 }) => match.Groups[3].Value,
                (":+" or "+", { Length: > 0 }) => match.Groups[3].Value,
                (":+" or "+", _) => string.Empty,
                (_, { } value) => value,
                _ => match.Value,
            };
        });

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex Words();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(\w+)(=(\S*))?$")]
    private static partial System.Text.RegularExpressions.Regex ArgWord();

    [System.Text.RegularExpressions.GeneratedRegex(@"<<-?\s*[""']?\w+")]
    private static partial System.Text.RegularExpressions.Regex Heredoc();

    [System.Text.RegularExpressions.GeneratedRegex(@"\\[ \t]*\n")]
    private static partial System.Text.RegularExpressions.Regex Continuation();

    [System.Text.RegularExpressions.GeneratedRegex(@"\A(?:#[^\n]*\n)*?#\s*escape\s*=\s*(\S)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex EscapeDirective();

    [System.Text.RegularExpressions.GeneratedRegex(@"\$\{(\w+)(?:(:?[-+])([^}]*))?\}|\$(\w+)")]
    private static partial System.Text.RegularExpressions.Regex Variable();

    private static readonly string[] DefaultRegistryPrefixes = ["docker.io/library/", "docker.io/", "library/"];

    private static string Normalised(string image)
    {
        var bare = image.Split('@')[0];
        bare = bare.LastIndexOf(':') > bare.LastIndexOf('/') ? bare[..bare.LastIndexOf(':')] : bare;
        var prefix = DefaultRegistryPrefixes.FirstOrDefault(candidate => bare.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
        return prefix is null ? bare : bare[prefix.Length..];
    }

    // The lines that a patch adds
    private static string? PipelineGuard(BranchFile file, IReadOnlyDictionary<string, string?>? before, IReadOnlyDictionary<string, string?>? after)
    {
        var added = AddedLines(file.Patch).ToList();
        var removed = RemovedLines(file.Patch).ToList();
        if (after is null || !WorkflowStructure.IsWorkflow(file.Path))
        {
            return PipelineChanges.Guard(file.Path, null, null, added, removed);
        }
        // What could not be read is not judged as if it were fine
        if (!after.TryGetValue(file.Path, out var now) || now is null)
        {
            return $"the pipeline '{file.Path}' could not be read at the commit of the change: it cannot be judged";
        }
        string? was = null;
        if (before is not null && before.TryGetValue(file.Path, out var old))
        {
            if (old is null)
            {
                return $"the pipeline '{file.Path}' could not be read before the change: what the change takes out of it cannot be judged";
            }
            was = old;
        }
        return PipelineChanges.Guard(file.Path, was, now, added, removed);
    }

    private static IEnumerable<string> AddedLines(string? patch) =>
        (patch ?? string.Empty).Split('\n').Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)).Select(line => line[1..]);

    // The lines that a patch takes out
    private static IEnumerable<string> RemovedLines(string? patch) =>
        (patch ?? string.Empty).Split('\n').Where(line => line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal)).Select(line => line[1..]);

    // The changed lines of a patch (those that start with a plus or a minus), as the controller reads them
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
