using System.Globalization;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor;

/// <summary>
/// Tries to repair a pull request, in the workshop and on paper: it validates the pull request first (nothing to repair when it passes), then lets an
/// agent edit the code in a container, refuses a change to a protected file, **checks the repair on a clean checkout of the approved commit with exactly
/// the patch applied**, and puts the patch in the trace. The patch is the result: nothing is pushed to the repository. What the agent says is not
/// believed: the executor runs the steps itself, and only their result counts.
/// <para>
/// The agent has no shell and cannot read outside the folder of the run, it does not see the instructions of the pull request (its <c>CLAUDE.md</c>, its
/// <c>.claude</c> folder, its MCP file are removed and left out of the patch), and the token of the subscription is the only secret of its container, which
/// is replaced by <c>***</c> in every line of the trace. Git runs on the host with its directory outside the mounted folder (see <see cref="PullRequestWorkspace"/>).
/// </para>
/// </summary>
public sealed class RepairPullRequestRunner(ValidationOptions validation, RepairOptions repair, IProcessRunner processes) : ITaskRunner
{
    private const int PatchChunk = 3500;

    private const int MaxPatchChunks = 8;

    // The largest patch that is kept and applied: an agent that rewrites the repository has not made the smallest change
    private const long MaxPatchBytes = 1_000_000;

    // What the pull request says to an agent about itself is removed before the agent starts, and is never part of the patch
    private static readonly string[] InstructionFiles = ["CLAUDE.md", "CLAUDE.local.md", ".mcp.json"];

    private static readonly string[] InstructionFolders = [".claude"];

    private static readonly string[] PatchExcludes =
    [
        ":(exclude,glob)**/CLAUDE.md", ":(exclude,glob)**/CLAUDE.local.md", ":(exclude,glob)**/.mcp.json", ":(exclude,glob)**/.claude/**",
    ];

    // The agent reads the code, not the container: its own state, the process table, the system
    private static readonly string[] ForbiddenReads = ["Read(//proc/**)", "Read(//sys/**)", "Read(//etc/**)", "Read(//tmp/**)", "Read(//root/**)"];

    private readonly PullRequestWorkspace _workspace = new(validation, processes);

    public TaskKind Kind => TaskKind.RepairPullRequest;

    // The patch stays in the trace; the agent calls the model of the subscription but writes nowhere that outlasts the run
    public bool ChangesNothing => true;

    public async ValueTask<TaskOutcome> RunAsync(TaskSpec task, RunTrace trace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(trace);
        if (_workspace.ProfileFor(task.Repository) is not { } profile)
        {
            return ValidatePullRequestRunner.Refuse(task, trace, "no validation profile for this repository");
        }

        if (_workspace.GitToken() is { } gitToken)
        {
            trace.Redact(gitToken);
        }
        trace.Add(RunEvents.TaskStarted, task.Id, $"repairing {task.Repository}#{task.PullRequest} at {PullRequestWorkspace.Short(task.HeadSha)}: validate, then let an agent try, then validate again on a clean checkout");
        var folders = _workspace.FoldersFor(task);
        try
        {
            if (await _workspace.FetchAsync(task, trace, folders, cancellationToken) is { } reason)
            {
                return ValidatePullRequestRunner.Refuse(task, trace, reason);
            }

            var before = await _workspace.RunStepsAsync(task, trace, profile, folders.Work, "before", cancellationToken);
            if (before.Result == TaskResult.Refused)
            {
                return ValidatePullRequestRunner.Refuse(task, trace, before.Reason);
            }
            if (before.Result == TaskResult.Passed)
            {
                return ValidatePullRequestRunner.Finish(task, trace, TaskResult.Passed, "nothing to repair: the validation passes");
            }

            return await TryRepairAsync(task, trace, profile, folders, before, cancellationToken);
        }
        finally
        {
            PullRequestWorkspace.Clean(folders, task, trace);
        }
    }

    private async Task<TaskOutcome> TryRepairAsync(TaskSpec task, RunTrace trace, ValidationProfile profile, RunFolders folders, StepsOutcome before, CancellationToken cancellationToken)
    {
        var token = (await File.ReadAllTextAsync(repair.TokenFile, cancellationToken)).Trim();
        trace.Redact(token);
        RemoveInstructions(folders.Work);
        var container = $"orchestrarr-agent-{task.Id:N}";
        // What the agent does is written in the trace as it happens; the lines may arrive from another thread
        var shown = 0;
        var left = 0;
        (string Text, string Turns, bool IsError)? final = null;
        void OnLine(string line)
        {
            final ??= AgentTranscript.Result(line);
            foreach (var text in AgentTranscript.Describe(line))
            {
                if (Interlocked.Increment(ref shown) <= repair.MaxTraceLines)
                {
                    trace.Add(RunEvents.TaskAgent, task.Id, text);
                }
                else
                {
                    Interlocked.Increment(ref left);
                }
            }
        }

        var run = await processes.RunAsync("docker", AgentArguments(task, folders, container, before), null, new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = token }, TimeSpan.FromMinutes(Math.Max(1, repair.TimeoutMinutes)), repair.MaxOutputCharacters, cancellationToken, OnLine);
        if (left > 0)
        {
            trace.Add(RunEvents.TaskAgent, task.Id, $"{left.ToString(CultureInfo.InvariantCulture)} more line(s) of the agent's work are not shown");
        }
        if (run.TimedOut)
        {
            await _workspace.KillAsync(container);
            trace.Add(RunEvents.TaskStep, task.Id, $"agent: stopped after {repair.TimeoutMinutes} min");
            return ValidatePullRequestRunner.Finish(task, trace, TaskResult.Failed, $"the agent took more than {repair.TimeoutMinutes} min");
        }
        if (run.ExitCode != 0)
        {
            // Never what the agent printed in the reason: it can carry anything. The trace keeps the end of it, cut.
            trace.Add(RunEvents.TaskStep, task.Id, $"agent: exit {run.ExitCode}\n{Tail(run.OutputTail)}");
            return PullRequestWorkspace.DockerCouldNotRun(run.ExitCode)
                ? ValidatePullRequestRunner.Refuse(task, trace, "the agent's container could not be started")
                : ValidatePullRequestRunner.Finish(task, trace, TaskResult.Failed, "the agent ended with an error");
        }
        trace.Add(RunEvents.TaskStep, task.Id, $"agent: done in {final?.Turns ?? "?"} turns{(final is { IsError: true } ? ", with an error" : string.Empty)}\n{final?.Text}");

        // The host reads the work tree only after the agent: a .git that the container made is not trusted, and git is given its own directory
        PullRequestWorkspace.RemoveGitEntry(folders.Work);
        if (await StagedPathsAsync(folders, cancellationToken) is not { } paths || paths.Count == 0)
        {
            return ValidatePullRequestRunner.Finish(task, trace, TaskResult.Failed, "the agent made no change");
        }
        if (paths.FirstOrDefault(path => repair.ProtectedPaths.Any(pattern => TaskShape.Matches(pattern, path))) is { } protectedPath)
        {
            trace.Add(RunEvents.TaskStep, task.Id, $"agent: it changed a protected file, {protectedPath}: the repair is refused");
            return ValidatePullRequestRunner.Finish(task, trace, TaskResult.Failed, "the agent changed a protected file");
        }

        var patch = await WritePatchAsync(folders, cancellationToken);
        if (patch is null)
        {
            return ValidatePullRequestRunner.Finish(task, trace, TaskResult.Failed, "the patch could not be made or is too large");
        }

        // The executor checks the repair itself, on the approved commit with exactly this patch: the agent saying that it works is not a fact, and neither is a folder
        // that the steps and the agent left behind
        if (await _workspace.CleanCopyWithPatchAsync(folders, cancellationToken) is { } notApplied)
        {
            trace.Add(RunEvents.TaskStep, task.Id, $"verify: {notApplied}");
            return ValidatePullRequestRunner.Finish(task, trace, TaskResult.Failed, notApplied);
        }
        var after = await _workspace.RunStepsAsync(task, trace, profile, folders.WorkAfter, "after", cancellationToken);
        if (after.Result != TaskResult.Passed)
        {
            return after.Result == TaskResult.Refused
                ? ValidatePullRequestRunner.Refuse(task, trace, after.Reason)
                : ValidatePullRequestRunner.Finish(task, trace, TaskResult.Failed, "the agent's change does not make the validation pass: " + after.Reason);
        }

        AddPatch(task, trace, patch, paths.Count);
        return ValidatePullRequestRunner.Finish(task, trace, TaskResult.Repaired, $"repaired: {paths.Count.ToString(CultureInfo.InvariantCulture)} file(s) changed, the validation passes again on a clean checkout (patch in the trace, nothing pushed)");
    }

    // The files the agent changed, as git sees them (a rename counts as the deletion of the old path and the creation of the new one)
    private async Task<IReadOnlyList<string>?> StagedPathsAsync(RunFolders folders, CancellationToken cancellationToken)
    {
        var add = await _workspace.GitAsync(folders, folders.Work, ["add", "-A", "--", ".", .. PatchExcludes], network: false, 200, cancellationToken);
        var names = await _workspace.GitAsync(folders, folders.Work, ["diff", "--cached", "--name-only", "--no-renames", "--no-ext-diff", "--", ".", .. PatchExcludes], network: false, 4000, cancellationToken);
        return add.ExitCode != 0 || names.ExitCode != 0 ? null : names.OutputTail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // Written to a file outside every mount, in full: what is shown in the trace may be cut, what is applied is not
    private async Task<string?> WritePatchAsync(RunFolders folders, CancellationToken cancellationToken)
    {
        var diff = await _workspace.GitAsync(
            folders, folders.Work, ["diff", "--cached", "--binary", "--no-renames", "--no-ext-diff", "--no-textconv", "--output", folders.PatchFile, "--", ".", .. PatchExcludes], network: false, 200, cancellationToken);
        if (diff.ExitCode != 0 || !File.Exists(folders.PatchFile) || new FileInfo(folders.PatchFile).Length is 0 or > MaxPatchBytes)
        {
            return null;
        }
        return await File.ReadAllTextAsync(folders.PatchFile, cancellationToken);
    }

    // The instructions of the pull request are for whoever reads it, not for the agent: they are taken out of the folder it works in
    private static void RemoveInstructions(string work)
    {
        var pending = new Stack<string>([work]);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (InstructionFolders.Contains(name, StringComparer.Ordinal))
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                    else if (name is not ("node_modules" or ".git") && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(entry);
                    }
                }
                else if (InstructionFiles.Contains(name, StringComparer.Ordinal))
                {
                    File.Delete(entry);
                }
            }
        }
    }

    private static void AddPatch(TaskSpec task, RunTrace trace, string patch, int files)
    {
        var chunks = Math.Min(MaxPatchChunks, (patch.Length + PatchChunk - 1) / PatchChunk);
        for (var index = 0; index < chunks; index++)
        {
            var part = patch.Substring(index * PatchChunk, Math.Min(PatchChunk, patch.Length - (index * PatchChunk)));
            trace.Add(RunEvents.TaskPatch, task.Id, $"patch {(index + 1).ToString(CultureInfo.InvariantCulture)}/{chunks.ToString(CultureInfo.InvariantCulture)} ({files.ToString(CultureInfo.InvariantCulture)} file(s))\n{part}");
        }
    }

    private List<string> AgentArguments(TaskSpec task, RunFolders folders, string container, StepsOutcome before)
    {
        // A marker that is different for each run, and that the output cannot contain: the output of the failing step is written by whoever wrote the pull request
        var marker = $"OUTPUT-{Guid.NewGuid():N}";
        var output = (before.FailedOutput ?? string.Empty).Replace(marker, string.Empty, StringComparison.Ordinal);
        var prompt = repair.Prompt
            .Replace("{repository}", task.Repository, StringComparison.Ordinal)
            .Replace("{number}", task.PullRequest.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{step}", before.FailedStep ?? "validation", StringComparison.Ordinal)
            .Replace("{protected}", string.Join(", ", repair.ProtectedPaths), StringComparison.Ordinal)
            .Replace("{marker}", marker, StringComparison.Ordinal)
            .Replace("{output}", output, StringComparison.Ordinal);
        // The only secret of the container is the token of the subscription, passed by name: its value is in the environment of the docker client only
        List<string> arguments = [.. _workspace.SandboxArguments(container, folders.Work, repair.Cpus, repair.MemoryMegabytes, network: true, ["CLAUDE_CODE_OAUTH_TOKEN"])];
        arguments.AddRange(
        [
            repair.Image,
            "claude", "-p", prompt,
            "--model", repair.Model,
            "--permission-mode", "acceptEdits",
            "--max-turns", repair.MaxTurns.ToString(CultureInfo.InvariantCulture),
            "--output-format", "stream-json", "--verbose",
            // Only the user's settings (there are none in the container), no MCP server but those given: nothing that the pull request brings widens the tools
            "--setting-sources", "user",
            "--strict-mcp-config",
            // Read and edit the code, nothing else: no shell (the executor runs the steps), no network tool, no reading of the container
            "--allowedTools", "Read", "Edit", "Glob", "Grep",
            "--disallowedTools", "Bash", "WebFetch", "WebSearch", .. ForbiddenReads,
        ]);
        return arguments;
    }

    private static string Tail(string text) => text.Length > 1500 ? text[^1500..] : text;
}
