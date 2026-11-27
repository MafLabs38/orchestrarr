using System.Text.RegularExpressions;

namespace Orchestrarr.Core.Missions;

/// <summary>
/// When a change may touch the pipelines, and what it may never do to them (ADR 0055 point 7, as built after the reviews of 2026-11-27). Changing a pipeline is maintenance, but a pipeline is also what judges a change: so (1) only the operations
/// whose object is the pipeline may touch it (a red pipeline, a pull request that raises a tool of the pipeline); every other operation has the pipelines in the frame of its mission, as before; and (2) a change that may touch them
/// never **weakens** them: no step that cannot fail, no job switched off, no wider token, no trigger that runs with the base's credentials, no new secret read. Code, not a prompt, holds that line.
/// </summary>
public static partial class PipelineChanges
{
    /// <summary>How a red pipeline is repaired, said once for the lead's plan and for the fix of a merge that turned the integration branch red (one text, so that the two never drift).</summary>
    public const string RepairRules = "A pipeline is repaired by the fix that a good developer makes: a test that fails only some of the time is made deterministic (a seed, a fixed value, no dependence on chance or on time), never retried until it passes; a check or a smoke test is never weakened to pass (what it checked, it still checks: the application is fixed, or what it needs is given to it in the pipeline); and a step that needs a secret that is not set runs only when the secret is there.";

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
        return entry.Brief is { Kind: "FailingCi" } or { Nature: "PipelineToolUpdate" or "BaseImageUpdate" };
    }

    /// <summary>The paths that the frame of this operation's missions forbids beyond the floor: the pipelines, unless the operation is about them.</summary>
    public static IReadOnlyList<string> ForbiddenFor(PlanEntry entry) => MayChange(entry) ? [] : Paths;

    /// <summary>Whether a path is a pipeline file.</summary>
    public static bool IsPipeline(string path) => PathPatterns.AnyMatches(Paths, [path]);

    /// <summary>Why these lines, added to a pipeline file, weaken it; null when they do not. Read on the lines that the change adds (a patch), or on a whole file when there is no base to compare with.</summary>
    public static string? Weakening(string path, IEnumerable<string> addedLines)
    {
        ArgumentNullException.ThrowIfNull(addedLines);
        foreach (var line in addedLines)
        {
            if (Weakens().Match(line) is { Success: true } found)
            {
                return $"the change weakens the pipeline '{path}' ({found.Value.Trim()}): a pipeline is what judges a change, so a step that cannot fail, a job switched off, a wider token, a trigger with the base's credentials or a new secret is a person's decision";
            }
        }
        return null;
    }

    // What weakens a pipeline: a step or a job that cannot fail or never runs, a command made to succeed whatever happens, a token with write rights, a trigger that runs with the credentials of the base branch, a secret that a job reads
    [GeneratedRegex(@"continue-on-error:\s*true|^\s*if:\s*(false|\$\{\{\s*false\s*\}\})\s*$|\|\|\s*(true|:|exit\s+0)\b|^\s*-?\s*run:\s*(true|:|exit\s+0)\s*$|pull_request_target|workflow_run|write-all|^\s*[a-z-]+:\s*write\s*$|\$\{\{\s*secrets\.|allow_failure:\s*true|failure:\s*ignore", RegexOptions.IgnoreCase | RegexOptions.Multiline, 250)]
    private static partial Regex Weakens();
}
