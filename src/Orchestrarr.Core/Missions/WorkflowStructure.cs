using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Orchestrarr.Core.Missions;

/// <summary>
/// A GitHub Actions workflow **read as what it is, YAML, and compared before and after a change** (ADR 0057): the jobs, their steps, what may fail without failing them, when they run, the matrix they run on, the rights of their
/// token and what triggers them. A change that takes any of it away, or widens what the token may do, is refused: what a pipeline checks is not taken out of it to make it pass. What a step runs (its shell) is text and stays the
/// line-based net's (<see cref="PipelineChanges"/>). Pure.
/// </summary>
public static class WorkflowStructure
{
    // Triggers that run a workflow with the credentials of the base branch on what someone else wrote
    private static readonly string[] DangerousTriggers = ["pull_request_target", "workflow_run", "issue_comment", "pull_request_review_comment"];

    // The conditions under which a step or a job still runs whenever the ones before it passed
    private static readonly string[] AlwaysRuns = ["success()", "always()", "!cancelled()"];

    // A workflow is a few hundred lines: far more than that is not read
    private const int MaxEvents = 20_000;

    private const int MaxDepth = 64;

    private static readonly string[] NeverRuns = ["false", "0", "''", "\"\"", "null"];

    // The shells the forge knows: any other value is a template of its own, which can make every step pass
    private static readonly string[] KnownShells = ["bash", "sh", "pwsh", "powershell", "python", "cmd"];

    /// <summary>Whether the path is a workflow of the forge (the only place GitHub reads them: <c>.github/workflows/*.yml</c> or <c>.yaml</c> at the root).</summary>
    public static bool IsWorkflow(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.StartsWith(".github/workflows/", StringComparison.Ordinal) && path.Count(character => character == '/') == 2
            && (path.EndsWith(".yml", StringComparison.Ordinal) || path.EndsWith(".yaml", StringComparison.Ordinal));
    }

    /// <summary>Why the workflow after the change checks less, or runs with more rights, than before it; null when it does not. <paramref name="before"/> is null for a new file (or one that did not read as a workflow).</summary>
    /// <param name="path">The path of the workflow in the repository.</param>
    /// <param name="before">The workflow at the merge base, or null for a new file.</param>
    /// <param name="after">The workflow at the commit of the change.</param>
    /// <param name="files">The files of the repository that the change did not add, when the forge listed them: a change of a trigger's path filters is then measured on them, and only a filter that covered none of them, made to cover some, passes (ADR 0057); without them it is refused.</param>
    public static string? Weakening(string path, string? before, string after, IReadOnlyCollection<string>? files = null)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (Read(after) is not { } next)
        {
            return $"the pipeline '{path}' does not read as a workflow: what cannot be read cannot be judged";
        }
        // A workflow before the change that does not read is not compared as an empty one (that would let anything be taken out of it): the change is refused, and a person reads it
        Workflow previous;
        if (before is null)
        {
            previous = Workflow.Empty;
        }
        else if (Read(before) is { } read)
        {
            previous = read;
        }
        else
        {
            return $"the change weakens the pipeline '{path}': it cannot be told, since the workflow before the change does not read here (an alias, or a size or a depth that no workflow has)";
        }
        var reason = NewDangerousTrigger(previous, next) ?? LostTrigger(previous, next, files) ?? NewShell(previous, next) ?? SwappedAction(previous, next) ?? Widened(previous.Permissions, next.Permissions, "the workflow");
        foreach (var (name, job) in next.Jobs)
        {
            reason ??= previous.Jobs.TryGetValue(name, out var was) ? JobWeakening(name, was, job, previous, next) : NewJobWeakening(name, job, previous, next);
        }
        reason ??= previous.Jobs.Keys.Where(name => !next.Jobs.ContainsKey(name)).Select(name => $"removes the job '{name}'").FirstOrDefault();
        return reason is null ? null : $"the change weakens the pipeline '{path}': it {reason}; what a pipeline checks is not taken out of it to make it pass, and a wider token or trigger is a person's decision";
    }

    private static string? NewDangerousTrigger(Workflow previous, Workflow next) =>
        next.Triggers.Keys.Where(trigger => DangerousTriggers.Contains(trigger, StringComparer.Ordinal) && !previous.Triggers.ContainsKey(trigger)).Select(trigger => $"adds the trigger '{trigger}', which runs with the base's credentials").FirstOrDefault();

    // A workflow that no longer runs where it ran checks nothing there: a trigger removed, or its filters changed. A path filter that covered no file of the repository (a dead control) may be made to cover some, measured on the
    // files of the repository; any other change of the path filters, a change of its branches or tags, or one that cannot be measured, is refused
    private static string? LostTrigger(Workflow previous, Workflow next, IReadOnlyCollection<string>? files)
    {
        foreach (var (trigger, filters) in previous.Triggers)
        {
            if (!next.Triggers.TryGetValue(trigger, out var now))
            {
                return $"stops the workflow from running on '{trigger}'";
            }
            if (string.Equals(filters, now, StringComparison.Ordinal))
            {
                continue;
            }
            var was = previous.PathFilters.GetValueOrDefault(trigger, PathFilter.None);
            var becomes = next.PathFilters.GetValueOrDefault(trigger, PathFilter.None);
            if (!string.Equals(was.Other, becomes.Other, StringComparison.Ordinal))
            {
                return $"changes the branches or tags that the trigger '{trigger}' runs on";
            }
            // A dormant trigger that runs with the base's credentials stays dormant: waking it is a person's decision, never a mend
            if (DangerousTriggers.Contains(trigger, StringComparer.Ordinal))
            {
                return $"changes the path filters of the trigger '{trigger}', which runs with the base's credentials";
            }
            if (FilterChangeRefusal(was, becomes, files) is { } refusal)
            {
                return $"changes the path filters of the trigger '{trigger}' ({refusal})";
            }
            // A dormant workflow that can write or reads a secret stays dormant: waking it gives it powers, a person's decision
            if (next.HoldsPowers)
            {
                return $"wakes the workflow on '{trigger}' while it can write or reads a secret";
            }
        }
        return null;
    }

    // A pattern that stands for a filter that does not read as a list of names: no plain pattern contains a line break
    private const string Unreadable = "\n";

    // At most this many patterns, this long, on one trigger: more than any workflow writes
    private const int MaxPatterns = 50;
    private const int MaxPatternLength = 200;

    // The characters whose meaning in a path filter is the same for the forge and for PathGlob: letters, digits, '.', '_', '/', '-' and the '*' of '*' and '**' (a leading '!' negates). '?', '+', '[ ]' and '\' mean other things to the
    // forge, and a filter that uses them is not measured
    private static readonly System.Text.RegularExpressions.Regex PlainPattern = new("^!?[A-Za-z0-9._/*-]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // The one change of a path filter that mends a control (the maintainer, 2026-11-27): a filter that covered no file of the repository (a dead control) made to cover some. Any other change of the path filters is refused, and so is one
    // that cannot be measured exactly (no list of the files, a pattern the forge reads otherwise, both kinds of filter at once). Null when it is that mend
    private static string? FilterChangeRefusal(PathFilter was, PathFilter becomes, IReadOnlyCollection<string>? files)
    {
        if (files is null)
        {
            return "what they cover cannot be measured here";
        }
        if (!was.IsPlain || !becomes.IsPlain)
        {
            return "a pattern that cannot be measured exactly, or both paths and paths-ignore";
        }
        try
        {
            if (files.Any(was.Covers))
            {
                return "the filter was running on files of the repository: only a filter that covered none may be mended";
            }
            return files.Any(becomes.Covers) ? null : "the filter still covers no file of the repository";
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return "a pattern that cannot be measured";
        }
    }

    // An action is mended by another version of the same action, never replaced by another one: a change that takes an action out of the workflow and brings a new one in is refused (adding an action, or a version of one,
    // is judged by the controller). Only for a workflow that existed: a new workflow has nothing to swap
    private static string? SwappedAction(Workflow previous, Workflow next)
    {
        if (ReferenceEquals(previous, Workflow.Empty))
        {
            return null;
        }
        // Counted, not as a set: an action used twice and replaced in one place is a swap too
        var gone = previous.Actions.Where(used => next.Actions.GetValueOrDefault(used.Key) < used.Value).Select(used => used.Key).ToList();
        var come = next.Actions.Where(used => previous.Actions.GetValueOrDefault(used.Key) < used.Value).Select(used => used.Key).ToList();
        return gone.Count > 0 && come.Count > 0 ? $"replaces the action '{gone[0]}' with '{come[0]}' (an action is mended by another version of the same action)" : null;
    }

    private static string? NewShell(Workflow previous, Workflow next) =>
        next.CustomShells.Where(shell => !previous.CustomShells.Contains(shell)).Select(shell => $"runs its steps in a shell of its own ('{shell}'), which can make every step pass").FirstOrDefault();

    private static string? JobWeakening(string name, Job was, Job job, Workflow previous, Workflow next)
    {
        if (job.Steps < was.Steps)
        {
            return $"removes {was.Steps - job.Steps} step(s) of the job '{name}'";
        }
        if (job.AlwaysRunningSteps < was.AlwaysRunningSteps)
        {
            return $"makes a step of the job '{name}' run only sometimes";
        }
        if (job.CannotFailSteps > was.CannotFailSteps || (job.CannotFail && !was.CannotFail))
        {
            return $"lets a step or the job '{name}' fail without failing the pipeline";
        }
        if (!string.Equals(job.Condition, was.Condition, StringComparison.Ordinal) && !RunsAlways(job.Condition))
        {
            return $"changes when the job '{name}' runs";
        }
        if (job.MatrixValues < was.MatrixValues)
        {
            return $"narrows the matrix of the job '{name}'";
        }
        return Widened(was.Permissions ?? previous.Permissions, job.Permissions ?? next.Permissions, $"the job '{name}'");
    }

    // A job that the change adds checks more; it may still not fail whatever happens, or run with more rights than the workflow gave
    private static string? NewJobWeakening(string name, Job job, Workflow previous, Workflow next)
    {
        if (job.CannotFailSteps > 0 || job.CannotFail)
        {
            return $"adds the job '{name}' with a step that may fail without failing the pipeline";
        }
        return Widened(previous.Permissions, job.Permissions ?? next.Permissions, $"the job '{name}'");
    }

    // A token is widened when a restriction that was there is taken away, or when a right is written that the restriction did not give
    private static string? Widened(Dictionary<string, string>? was, Dictionary<string, string>? now, string what)
    {
        if (was is not null && now is null)
        {
            return $"takes the restriction of the token of {what} away";
        }
        if (now is null)
        {
            return null;
        }
        // Where nothing was said, the token had the repository's default, which the test bed keeps read-only: a right to write that is now written is a wider token, whatever the default is
        if (was is null)
        {
            if (now.FirstOrDefault(pair => pair.Value == "write") is not { Key: { } scope })
            {
                return null;
            }
            return scope == "*" ? $"widens the token of {what} (every right: write)" : $"widens the token of {what} ({scope}: write)";
        }
        foreach (var (scope, level) in now)
        {
            var before = was.TryGetValue(scope, out var given) ? given : was.GetValueOrDefault("*");
            if (level == "write" && before != "write")
            {
                return $"widens the token of {what} ({scope}: write)";
            }
        }
        return null;
    }

    private sealed record Job(int Steps, int AlwaysRunningSteps, int CannotFailSteps, bool CannotFail, string? Condition, int MatrixValues, Dictionary<string, string>? Permissions);

    private sealed record Workflow(Dictionary<string, string> Triggers, Dictionary<string, string>? Permissions, Dictionary<string, Job> Jobs, HashSet<string> CustomShells, Dictionary<string, PathFilter> PathFilters, Dictionary<string, int> Actions, bool ReadsSecrets = false)
    {
        public static Workflow Empty { get; } = new([], null, [], [], [], []);

        /// <summary>Whether the workflow, or one of its jobs, asks for a token that can write, reads a secret, or leaves its token to the repository's default (which may write).</summary>
        public bool HoldsPowers => ReadsSecrets || (Permissions is null && Jobs.Values.Any(job => job.Permissions is null)) || CanWrite(Permissions) || Jobs.Values.Any(job => CanWrite(job.Permissions));

        private static bool CanWrite(Dictionary<string, string>? permissions) => permissions is not null && permissions.Values.Any(level => level.Contains("write", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The path filters of a trigger (<c>paths</c> with its <c>!</c> negations, last match wins; <c>paths-ignore</c>), and the rest of its filters in one canonical text.</summary>
    private sealed record PathFilter(IReadOnlyList<string>? Paths, IReadOnlyList<string>? Ignore, string Other)
    {
        public static PathFilter None { get; } = new(null, null, string.Empty);

        /// <summary>Whether the filter can be measured exactly: one kind of filter, a bounded number of patterns, each of the plain characters only.</summary>
        public bool IsPlain
        {
            get
            {
                if (Paths is not null && Ignore is not null)
                {
                    return false;
                }
                var patterns = Paths ?? Ignore ?? [];
                // An empty list, or a list of negations only, is a shape whose reading by the forge is not certain: it is not measured
                if ((Paths ?? Ignore) is { Count: 0 } || Paths is not null && Paths.All(pattern => pattern.StartsWith('!')))
                {
                    return false;
                }
                return patterns.Count <= MaxPatterns && patterns.All(pattern => pattern.Length <= MaxPatternLength && PlainPattern.IsMatch(pattern) && (Paths is not null || !pattern.StartsWith('!')));
            }
        }

        /// <summary>Whether a change of this file makes the trigger run.</summary>
        public bool Covers(string file)
        {
            if (Paths is { } paths)
            {
                var covered = false;
                foreach (var pattern in paths)
                {
                    var negated = pattern.StartsWith('!');
                    if (Orchestrarr.Core.Infrastructure.PathGlob.IsMatch(negated ? pattern[1..] : pattern, file))
                    {
                        covered = !negated;
                    }
                }
                return covered;
            }
            return Ignore is not { } ignore || !ignore.Any(pattern => Orchestrarr.Core.Infrastructure.PathGlob.IsMatch(pattern, file));
        }
    }

    private static Workflow? Read(string text)
    {
        try
        {
            // An alias makes the graph share nodes, and a few lines of them can make a walk of it explode: a workflow that uses one is not read here (a person reads it)
            var events = new YamlDotNet.Core.Parser(new StringReader(text));
            var count = 0;
            var depth = 0;
            while (events.MoveNext())
            {
                depth += events.Current switch
                {
                    YamlDotNet.Core.Events.MappingStart or YamlDotNet.Core.Events.SequenceStart => 1,
                    YamlDotNet.Core.Events.MappingEnd or YamlDotNet.Core.Events.SequenceEnd => -1,
                    _ => 0,
                };
                // Nor is one nested deeper than any workflow, which a recursive reading could not walk
                if (events.Current is YamlDotNet.Core.Events.AnchorAlias || ++count > MaxEvents || depth > MaxDepth)
                {
                    return null;
                }
            }
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return null;
            }
            var jobs = new Dictionary<string, Job>(StringComparer.Ordinal);
            if (Child(root, "jobs") is YamlMappingNode jobNodes)
            {
                foreach (var (key, value) in jobNodes.Children)
                {
                    if (key is YamlScalarNode { Value: { } name } && value is YamlMappingNode job)
                    {
                        jobs[name] = JobOf(job);
                    }
                }
            }
            var shells = new HashSet<string>(StringComparer.Ordinal);
            CollectShells(root, shells);
            var actions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            CollectActions(root, actions);
            return new Workflow(TriggersOf(Child(root, "on")), PermissionsOf(Child(root, "permissions")), jobs, shells, PathFiltersOf(Child(root, "on")), actions, text.Contains("secrets", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception failure) when (failure is YamlException or ArgumentException or InvalidOperationException or InvalidCastException)
        {
            // A duplicate key, an alias that points nowhere, a node of an unexpected kind: what does not read is not a workflow, and the change is refused, never let through by an exception
            return null;
        }
    }

    private static Job JobOf(YamlMappingNode job)
    {
        var steps = Child(job, "steps") is YamlSequenceNode sequence ? sequence.Children.OfType<YamlMappingNode>().ToList() : [];
        // A job that calls a reusable workflow is one step that runs it
        var count = steps.Count + (Child(job, "uses") is YamlScalarNode ? 1 : 0);
        var always = steps.Count(step => RunsAlways(Scalar(Child(step, "if")))) + (Child(job, "uses") is YamlScalarNode ? 1 : 0);
        var cannotFail = steps.Count(step => MayPassAnyway(Child(step, "continue-on-error")));
        return new Job(count, always, cannotFail, MayPassAnyway(Child(job, "continue-on-error")), Normalised(Scalar(Child(job, "if"))), MatrixSize(Child(Child(job, "strategy"), "matrix")), PermissionsOf(Child(job, "permissions")));
    }

    // Each trigger with its filters, written in one canonical text (empty when it has none)
    private static Dictionary<string, string> TriggersOf(YamlNode? node) => node switch
    {
        YamlScalarNode { Value: { } one } => new(StringComparer.Ordinal) { [one] = string.Empty },
        YamlSequenceNode many => many.Children.OfType<YamlScalarNode>().Select(item => item.Value).OfType<string>().Distinct(StringComparer.Ordinal).ToDictionary(name => name, _ => string.Empty, StringComparer.Ordinal),
        YamlMappingNode map => map.Children.Where(pair => pair.Key is YamlScalarNode { Value: not null })
            .ToDictionary(pair => ((YamlScalarNode)pair.Key).Value!, pair => Canonical(pair.Value), StringComparer.Ordinal),
        _ => new(StringComparer.Ordinal),
    };

    // The path filters of each trigger written as a mapping (a trigger written as a name or in a list has none)
    private static Dictionary<string, PathFilter> PathFiltersOf(YamlNode? node)
    {
        var found = new Dictionary<string, PathFilter>(StringComparer.Ordinal);
        if (node is not YamlMappingNode map)
        {
            return found;
        }
        foreach (var (key, value) in map.Children)
        {
            if (key is not YamlScalarNode { Value: { } trigger } || value is not YamlMappingNode filters)
            {
                continue;
            }
            // A filter written in a shape the forge reads otherwise (a mapping, a list of lists) is kept as a pattern that is never plain: it is not measured
            IReadOnlyList<string>? List(string name) => Child(filters, name) switch
            {
                null => null,
                YamlSequenceNode items when items.Children.All(item => item is YamlScalarNode { Value: not null }) => [.. items.Children.Cast<YamlScalarNode>().Select(item => item.Value ?? string.Empty)],
                YamlScalarNode { Value: { } one } => [one],
                _ => [Unreadable],
            };
            var other = new YamlMappingNode(filters.Children.Where(pair => pair.Key is not YamlScalarNode { Value: "paths" or "paths-ignore" }));
            // A trigger with no other filter compares equal to one written as a bare name
            found[trigger] = new PathFilter(List("paths"), List("paths-ignore"), other.Children.Count == 0 ? string.Empty : Canonical(other));
        }
        return found;
    }

    private static string Canonical(YamlNode node) => node switch
    {
        YamlScalarNode scalar => scalar.Value ?? string.Empty,
        YamlSequenceNode list => "[" + string.Join(",", list.Children.Select(Canonical)) + "]",
        YamlMappingNode map => "{" + string.Join(",", map.Children.OrderBy(pair => (pair.Key as YamlScalarNode)?.Value, StringComparer.Ordinal).Select(pair => $"{Canonical(pair.Key)}:{Canonical(pair.Value)}")) + "}",
        _ => string.Empty,
    };

    // Every action a step or a job uses, without its version: `owner/name` (and a path inside it), a local action or a container image as written
    private static void CollectActions(YamlNode node, Dictionary<string, int> found)
    {
        switch (node)
        {
            case YamlMappingNode map:
                foreach (var (key, value) in map.Children)
                {
                    if (key is YamlScalarNode { Value: "uses" } && value is YamlScalarNode { Value: { } used })
                    {
                        var name = used.Split('@')[0].Trim();
                        found[name] = found.GetValueOrDefault(name) + 1;
                    }
                    else
                    {
                        CollectActions(value, found);
                    }
                }
                break;
            case YamlSequenceNode list:
                foreach (var item in list.Children)
                {
                    CollectActions(item, found);
                }
                break;
        }
    }

    // Every `shell` of the workflow (its defaults, a job's defaults, a step) that is not a shell the forge knows
    private static void CollectShells(YamlNode node, HashSet<string> found)
    {
        switch (node)
        {
            case YamlMappingNode map:
                foreach (var (key, value) in map.Children)
                {
                    if (key is YamlScalarNode { Value: "shell" } && value is YamlScalarNode { Value: { } shell } && !KnownShells.Contains(shell.Trim(), StringComparer.Ordinal))
                    {
                        _ = found.Add(shell.Trim());
                    }
                    CollectShells(value, found);
                }
                break;
            case YamlSequenceNode list:
                foreach (var item in list.Children)
                {
                    CollectShells(item, found);
                }
                break;
        }
    }

    private static Dictionary<string, string>? PermissionsOf(YamlNode? node) => node switch
    {
        YamlScalarNode { Value: "write-all" } => new(StringComparer.Ordinal) { ["*"] = "write" },
        YamlScalarNode { Value: "read-all" } => new(StringComparer.Ordinal) { ["*"] = "read" },
        YamlScalarNode => new(StringComparer.Ordinal),
        YamlMappingNode map => map.Children.Where(pair => pair.Key is YamlScalarNode && pair.Value is YamlScalarNode)
            .ToDictionary(pair => ((YamlScalarNode)pair.Key).Value ?? string.Empty, pair => ((YamlScalarNode)pair.Value).Value ?? string.Empty, StringComparer.Ordinal),
        _ => null,
    };

    // How many runs the matrix makes: the product of the lengths of its axes (a value that is not a list is one), plus the entries it includes, less the ones it excludes
    private static int MatrixSize(YamlNode? node)
    {
        if (node is not YamlMappingNode matrix)
        {
            return 0;
        }
        long product = 1;
        var included = 0;
        var excluded = 0;
        foreach (var (key, value) in matrix.Children)
        {
            var size = value is YamlSequenceNode list ? list.Children.Count : 1;
            switch ((key as YamlScalarNode)?.Value)
            {
                case "include":
                    included += size;
                    break;
                case "exclude":
                    excluded += size;
                    break;
                default:
                    // Saturated, never wrapped: a guard that compares sizes must not read a huge matrix as a small one
                    product = Math.Min(product * size, int.MaxValue);
                    break;
            }
        }
        return (int)Math.Clamp(product + included - excluded, int.MinValue, int.MaxValue);
    }

    private static bool MayPassAnyway(YamlNode? node) => node is not null && !string.Equals(Normalised(Scalar(node)), "false", StringComparison.Ordinal);

    private static bool RunsAlways(string? condition) => Normalised(condition) is not { } normal || AlwaysRuns.Contains(normal, StringComparer.Ordinal);

    // A condition as the forge evaluates it: the expression marks and the spaces do not count; a constant that never holds is said as such, so that any spelling of it reads the same
    private static string? Normalised(string? condition)
    {
        if (condition is null)
        {
            return null;
        }
        var text = condition.Trim();
        if (text.StartsWith("${{", StringComparison.Ordinal) && text.EndsWith("}}", StringComparison.Ordinal))
        {
            text = text[3..^2];
        }
        text = string.Concat(text.Where(character => !char.IsWhiteSpace(character))).ToLowerInvariant();
        return NeverRuns.Contains(text, StringComparer.Ordinal) ? "false" : text;
    }

    // A value that is not a plain scalar (a mapping, a list) is something, never nothing
    private static string? Scalar(YamlNode? node) => node switch
    {
        YamlScalarNode scalar => scalar.Value,
        null => null,
        _ => "expression",
    };

    private static YamlNode? Child(YamlNode? node, string key) =>
        node is YamlMappingNode map && map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
}
