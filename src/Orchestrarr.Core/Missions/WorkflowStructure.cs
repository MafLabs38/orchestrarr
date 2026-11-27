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
    public static string? Weakening(string path, string? before, string after)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (Read(after) is not { } next)
        {
            return $"the pipeline '{path}' does not read as a workflow: what cannot be read cannot be judged";
        }
        var previous = (before is null ? null : Read(before)) ?? Workflow.Empty;
        var reason = NewDangerousTrigger(previous, next) ?? LostTrigger(previous, next) ?? NewShell(previous, next) ?? Widened(previous.Permissions, next.Permissions, "the workflow");
        foreach (var (name, job) in next.Jobs)
        {
            reason ??= previous.Jobs.TryGetValue(name, out var was) ? JobWeakening(name, was, job, previous, next) : NewJobWeakening(name, job, previous, next);
        }
        reason ??= previous.Jobs.Keys.Where(name => !next.Jobs.ContainsKey(name)).Select(name => $"removes the job '{name}'").FirstOrDefault();
        return reason is null ? null : $"the change weakens the pipeline '{path}': it {reason}; what a pipeline checks is not taken out of it to make it pass, and a wider token or trigger is a person's decision";
    }

    private static string? NewDangerousTrigger(Workflow previous, Workflow next) =>
        next.Triggers.Keys.Where(trigger => DangerousTriggers.Contains(trigger, StringComparer.Ordinal) && !previous.Triggers.ContainsKey(trigger)).Select(trigger => $"adds the trigger '{trigger}', which runs with the base's credentials").FirstOrDefault();

    // A workflow that no longer runs where it ran checks nothing there: a trigger removed, or its filters (branches, paths, tags) changed
    private static string? LostTrigger(Workflow previous, Workflow next)
    {
        foreach (var (trigger, filters) in previous.Triggers)
        {
            if (!next.Triggers.TryGetValue(trigger, out var now))
            {
                return $"stops the workflow from running on '{trigger}'";
            }
            if (!string.Equals(filters, now, StringComparison.Ordinal))
            {
                return $"changes the filters of the trigger '{trigger}'";
            }
        }
        return null;
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

    private sealed record Workflow(Dictionary<string, string> Triggers, Dictionary<string, string>? Permissions, Dictionary<string, Job> Jobs, HashSet<string> CustomShells)
    {
        public static Workflow Empty { get; } = new([], null, [], []);
    }

    private static Workflow? Read(string text)
    {
        try
        {
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
            return new Workflow(TriggersOf(Child(root, "on")), PermissionsOf(Child(root, "permissions")), jobs, shells);
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

    private static string Canonical(YamlNode node) => node switch
    {
        YamlScalarNode scalar => scalar.Value ?? string.Empty,
        YamlSequenceNode list => "[" + string.Join(",", list.Children.Select(Canonical)) + "]",
        YamlMappingNode map => "{" + string.Join(",", map.Children.OrderBy(pair => (pair.Key as YamlScalarNode)?.Value, StringComparer.Ordinal).Select(pair => $"{Canonical(pair.Key)}:{Canonical(pair.Value)}")) + "}",
        _ => string.Empty,
    };

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
        var product = 1;
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
                    product *= size;
                    break;
            }
        }
        return product + included - excluded;
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
