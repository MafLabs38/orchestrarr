using System.Text.RegularExpressions;
using Orchestrarr.Core.Decisions;

namespace Orchestrarr.Core.Missions;

/// <summary>
/// When a change may touch the pipelines, and what it may never do to them (ADR 0055 point 7, as built after the reviews of 2026-11-27). Changing a pipeline is maintenance, but a pipeline is also what judges a change: so (1) only the operations
/// whose object is the pipeline may touch it (a red pipeline, a pull request that raises a tool of the pipeline); every other operation has the pipelines in the frame of its mission, as before; and (2) a change that may touch them
/// never **weakens** them: no step that cannot fail, no job switched off, no wider token, no trigger that runs with the base's credentials, no new secret read. Code, not a prompt, holds that line.
/// </summary>
public static partial class PipelineChanges
{
    /// <summary>How a red pipeline is repaired, said once for the lead's plan and for the fix of a merge that turned the integration branch red (one text, so that the two never drift).</summary>
    public const string RepairRules = "A pipeline is repaired by the fix that a good developer makes: a test that fails only some of the time is made deterministic (a seed, a fixed value, no dependence on chance or on time), never retried until it passes; a check or a smoke test is never weakened to pass (what it checked, it still checks: the application is fixed, or what it needs is given to it in the pipeline); and a step that needs a secret that is not set is left as it is and said in the last answer (a new read of a secret is a person's decision).";

    /// <summary>The pipeline files of the forges and of the CI services: GitHub, GitLab, Gitea, Forgejo, Woodpecker, Drone, CircleCI, Jenkins, Azure Pipelines, Travis, Bitbucket.</summary>
    public static readonly IReadOnlyList<string> Paths =
    [
        ".github/**", "**/.github/**", ".gitlab-ci.yml", ".gitlab/**", ".gitea/**", ".forgejo/**", ".woodpecker/**", ".woodpecker.yml", ".drone.yml",
        ".circleci/**", "**/Jenkinsfile*", "azure-pipelines*.yml", "**/.travis.yml", "**/bitbucket-pipelines.yml",
    ];

    /// <summary>Whether this operation is about the pipeline itself: a red pipeline, a pull request that raises a tool of the pipeline, or the base image whose runtime the pipeline follows (ADR 0054's nature). The kind and the nature are what the control plane read of the row, never an agent's word.</summary>
    public static bool MayChange(PlanEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Brief is { Kind: nameof(ProblemKind.FailingCi) } or { Nature: nameof(ProblemNature.PipelineToolUpdate) or nameof(ProblemNature.BaseImageUpdate) };
    }

    /// <summary>The paths that the frame of this operation's missions forbids beyond the floor: the pipelines, unless the operation is about them.</summary>
    public static IReadOnlyList<string> ForbiddenFor(PlanEntry entry) => MayChange(entry) ? [] : Paths;

    /// <summary>
    /// Why a change that touches pipeline files is not kept when the agent did not say why each change to them is needed (ADR 0057); null when it touches none, or says why. Saying why is not enough: the controller checks what is
    /// said against the lines and the problem, as the second control of a pipeline change.
    /// </summary>
    public static string? Unjustified(IEnumerable<string> paths, string? justification)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var pipelines = paths.Where(IsPipeline).ToList();
        return pipelines.Count == 0 || !string.IsNullOrWhiteSpace(justification)
            ? null
            : $"the change touches the pipeline files ({string.Join(", ", pipelines)}) and does not say why each change to them is needed: a pipeline change is justified by the agent, then checked";
    }

    /// <summary>Whether a path is a pipeline file.</summary>
    public static bool IsPipeline(string path) => PathPatterns.AnyMatches(Paths, [path]);

    /// <summary>
    /// Why a change of a pipeline file weakens it, or null (ADR 0055, ADR 0057). A workflow of the forge whose text after the change is known is **read as YAML and compared with the file before it** (<see cref="WorkflowStructure"/>),
    /// and the lines it adds are read for what only text says (a shell command made to succeed, a secret read); any other pipeline file is read on its lines (<see cref="Weakening"/>, <see cref="Removal"/>).
    /// </summary>
    public static string? Guard(string path, string? before, string? after, IReadOnlyList<string> addedLines, IReadOnlyList<string> removedLines)
    {
        ArgumentNullException.ThrowIfNull(addedLines);
        ArgumentNullException.ThrowIfNull(removedLines);
        if (WorkflowStructure.IsWorkflow(path) && after is not null)
        {
            return WorkflowStructure.Weakening(path, before, after) ?? Matched(path, addedLines, ShellWeakens());
        }
        return Weakening(path, addedLines) ?? Removal(path, removedLines, addedLines);
    }

    /// <summary>Why these lines, added to a pipeline file, weaken it; null when they do not. Read on the lines that the change adds (a patch), or on a whole file when there is no base to compare with.</summary>
    public static string? Weakening(string path, IEnumerable<string> addedLines)
    {
        ArgumentNullException.ThrowIfNull(addedLines);
        return Matched(path, addedLines, Weakens());
    }

    private static string? Matched(string path, IEnumerable<string> addedLines, Regex net)
    {
        foreach (var line in addedLines)
        {
            if (net.Match(line) is { Success: true } found)
            {
                return $"the change weakens the pipeline '{path}' ({found.Value.Trim()}): a pipeline is what judges a change, so a step that cannot fail, a job switched off, a wider token, a trigger with the base's credentials or a new secret is a person's decision";
            }
        }
        return null;
    }

    /// <summary>
    /// Why a change of a pipeline file removes what it checks, or null: more steps (a <c>run</c>, a <c>uses</c>, a <c>script</c>, a <c>needs</c>) leave the file than come into it. Removing a step is the commonest way to make a red
    /// pipeline green; a change that replaces a step by another keeps the count. A net, not a proof: the controller reads the lines too.
    /// </summary>
    public static string? Removal(string path, IEnumerable<string> removedLines, IEnumerable<string> addedLines)
    {
        ArgumentNullException.ThrowIfNull(removedLines);
        ArgumentNullException.ThrowIfNull(addedLines);
        var removedList = removedLines.ToList();
        var addedList = addedLines.ToList();
        var removed = removedList.Count(line => Step().IsMatch(line));
        var added = addedList.Count(line => Step().IsMatch(line));
        if (removed > added)
        {
            return $"the change removes {removed - added} step(s) of the pipeline '{path}': what a pipeline checks is not taken out of it to make it pass, that is a person's decision";
        }
        // A token is widened by taking out what narrows it, too: a permissions block or a read-only scope
        return removedList.Count(line => Narrowing().IsMatch(line)) > addedList.Count(line => Narrowing().IsMatch(line))
            ? $"the change takes a restriction of the token out of the pipeline '{path}': a wider token is a person's decision"
            : null;
    }

    [GeneratedRegex(@"^\s*-?\s*(run|uses|script|needs)\s*:", RegexOptions.IgnoreCase, 250)]
    private static partial Regex Step();

    [GeneratedRegex(@"^\s*permissions\s*:|^\s*[a-z-]+\s*:\s*['""]?(read|none)['""]?\s*(#.*)?$|read-all", RegexOptions.IgnoreCase, 250)]
    private static partial Regex Narrowing();

    // What only the text of a step says: a command made to succeed whatever happens, the errors of a shell switched off, a secret that a job reads
    [GeneratedRegex(@"\|\|\s*(true|:|exit\s+0)\b|^\s*-?\s*run:\s*(true|:|exit\s+0)\s*$|\bset\s+\+e\b|\bsecrets\s*[.\[]|toJSON\(\s*secrets|secrets:\s*inherit", RegexOptions.IgnoreCase | RegexOptions.Multiline, 250)]
    private static partial Regex ShellWeakens();

    // What weakens a pipeline: a step or a job that cannot fail or never runs (in any spelling the line-based net knows; a net, not a proof), a command made to succeed whatever happens, a token with write rights, a trigger that runs with the credentials of the base branch, a secret that a job reads
    [GeneratedRegex(@"continue-on-error:\s*(\$\{\{\s*)?['""]?true|^\s*if:\s*(\$\{\{\s*)?(always\(\)\s*&&\s*)?['""]?false\b|\|\|\s*(true|:|exit\s+0)\b|^\s*-?\s*run:\s*(true|:|exit\s+0)\s*$|\bset\s+\+e\b|pull_request_target|workflow_run|issue_comment|write-all|^\s*[a-z-]+:\s*['""]?write['""]?\s*(#.*)?$|\{[^}]*:\s*write\b|\bsecrets\s*[.\[]|toJSON\(\s*secrets|secrets:\s*inherit|allow_failure:\s*true|failure:\s*ignore", RegexOptions.IgnoreCase | RegexOptions.Multiline, 250)]
    private static partial Regex Weakens();
}
