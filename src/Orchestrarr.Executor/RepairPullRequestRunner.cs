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

    internal static readonly string[] PatchExcludes =
    [
        ":(exclude,glob)**/CLAUDE.md", ":(exclude,glob)**/CLAUDE.local.md", ":(exclude,glob)**/.mcp.json", ":(exclude,glob)**/.claude/**",
    ];

    // The agent reads the code, not the container: its own state, the process table, the system
    // And not to write where the container keeps its own state or the system's files: the work folder is the only place the agent edits
    private static readonly string[] ForbiddenEdits = ["Edit(//proc/**)", "Edit(//dev/**)", "Edit(//sys/**)", "Edit(//etc/**)", "Edit(//tmp/**)", "Edit(//root/**)", "Edit(//home/**)", "Edit(//usr/**)"];

    private static readonly string[] ForbiddenReads = ["Read(//proc/**)", "Read(//dev/**)", "Read(//sys/**)", "Read(//etc/**)", "Read(//tmp/**)", "Read(//root/**)"];

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

        var folders = _workspace.FoldersFor(task);
        if (folders.GitToken is { } gitToken)
        {
            trace.Redact(gitToken);
        }
        trace.Add(RunEvents.TaskStarted, task.Id, $"repairing {task.Repository}#{task.PullRequest} at {PullRequestWorkspace.Short(task.HeadSha)}: validate, then let an agent try, then validate again on a clean checkout");
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
        string token;
        try
        {
            token = (await File.ReadAllTextAsync(repair.TokenFile, cancellationToken)).Trim();
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return ValidatePullRequestRunner.Refuse(task, trace, "the agent's token could not be read");
        }
        trace.Redact(token);
        var removed = RemoveInstructions(folders.Work);
        var container = $"orchestrarr-agent-{task.Id:N}";
        // What the agent does is written in the trace as it happens; the lines may arrive from another thread
        var shown = 0;
        var left = 0;
        (string Text, string Turns, bool IsError)? final = null;
        var finalGate = new Lock();
        void OnLine(string line)
        {
            if (AgentTranscript.Result(line) is { } result)
            {
                lock (finalGate)
                {
                    final ??= result;
                }
            }
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

        var run = await _workspace.RunContainerAsync(container, AgentArguments(task, folders, container, before), OnLine, repair.MaxOutputCharacters, cancellationToken, new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = token }, TimeSpan.FromMinutes(Math.Max(1, repair.TimeoutMinutes)));
        if (left > 0)
        {
            trace.Add(RunEvents.TaskAgent, task.Id, $"{left.ToString(CultureInfo.InvariantCulture)} more line(s) of the agent's work are not shown");
        }
        if (run.TimedOut)
        {
            if (!await _workspace.KillAsync(container))
            {
                return ValidatePullRequestRunner.Refuse(task, trace, "the agent's container could not be stopped");
            }
            trace.Add(RunEvents.TaskStep, task.Id, $"agent: stopped after {Math.Max(1, repair.TimeoutMinutes).ToString(CultureInfo.InvariantCulture)} min");
            return ValidatePullRequestRunner.Finish(task, trace, TaskResult.Failed, $"the agent took more than {Math.Max(1, repair.TimeoutMinutes).ToString(CultureInfo.InvariantCulture)} min");
        }
        if (run.ExitCode != 0)
        {
            // Never what the agent printed in the reason: it can carry anything. The trace keeps the end of it, cut.
            trace.Add(RunEvents.TaskStep, task.Id, $"agent: exit {run.ExitCode}\n{Tail(run.OutputTail)}");
            return PullRequestWorkspace.DockerCouldNotRun(run)
                ? ValidatePullRequestRunner.Refuse(task, trace, "the agent's container could not be started")
                : ValidatePullRequestRunner.Finish(task, trace, TaskResult.Failed, "the agent ended with an error");
        }
        trace.Add(RunEvents.TaskStep, task.Id, $"agent: done in {final?.Turns ?? "?"} turns{(final is { IsError: true } ? ", with an error" : string.Empty)}\n{final?.Text}");

        // The host reads the work tree only after the agent: a .git that the container made is not trusted, and git is given its own directory
        PullRequestWorkspace.RemoveGitEntry(folders.Work);
        // The same walk as before the agent: a .git at any depth, a link or an instruction file that the agent made is gone before git reads the tree
        removed.AddRange(RemoveInstructions(folders.Work));
        var excludes = PatchExcludes.Concat(removed.Select(path => ":(exclude,literal)" + path)).ToArray();
        var (paths, error) = await StagedPathsAsync(folders, excludes, cancellationToken);
        if (error is not null)
        {
            // A git that failed, or a list too large to read, is not "no change": it is said
            return ValidatePullRequestRunner.Finish(task, trace, TaskResult.Failed, error);
        }
        if (paths.Count == 0)
        {
            return ValidatePullRequestRunner.Finish(task, trace, TaskResult.Failed, "the agent made no change");
        }
        if (paths.FirstOrDefault(repair.IsProtected) is { } protectedPath)
        {
            trace.Add(RunEvents.TaskStep, task.Id, $"agent: it changed a protected file, {protectedPath}: the repair is refused");
            return ValidatePullRequestRunner.Finish(task, trace, TaskResult.Failed, "the agent changed a protected file");
        }

        var patch = await WritePatchAsync(folders, excludes, cancellationToken);
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

        AddPatch(task, trace, patch.Value, paths.Count);
        return ValidatePullRequestRunner.Finish(task, trace, TaskResult.Repaired, $"repaired: {paths.Count.ToString(CultureInfo.InvariantCulture)} file(s) changed, the validation passes again on a clean checkout (patch in the trace, nothing pushed)");
    }

    // The files the agent changed, as git sees them (a rename counts as the deletion of the old path and the creation of the new one)
    private async Task<(IReadOnlyList<string> Paths, string? Error)> StagedPathsAsync(RunFolders folders, string[] excludes, CancellationToken cancellationToken)
    {
        var add = await _workspace.GitAsync(folders, folders.Work, ["add", "-A", "--", ".", .. excludes], network: false, 200, cancellationToken);
        // Separated by NUL (git quotes a path with a quote or a control character otherwise, and a quoted path would not match a protected pattern). Written to a file by git and read in full: a long list read from the end of an output would lose its start, where the protected paths sort first
        var names = await _workspace.GitAsync(folders, folders.Work, ["diff", "--cached", "--name-only", "-z", "--no-renames", "--no-ext-diff", "--output", folders.PathsFile, "--", ".", .. excludes], network: false, 200, cancellationToken);
        if (add.ExitCode != 0 || names.ExitCode != 0 || !File.Exists(folders.PathsFile))
        {
            return ([], "git could not list what the agent changed");
        }
        if (new FileInfo(folders.PathsFile).Length > MaxPatchBytes)
        {
            return ([], "the agent changed too many files to be a small repair");
        }
        return ((await File.ReadAllTextAsync(folders.PathsFile, cancellationToken)).Split('\0', StringSplitOptions.RemoveEmptyEntries), null);
    }

    // Written to a file outside every mount, in full: what is shown in the trace may be cut, what is applied is not
    private async Task<(string Text, long Bytes, string Digest)?> WritePatchAsync(RunFolders folders, string[] excludes, CancellationToken cancellationToken)
    {
        var diff = await _workspace.GitAsync(
            folders, folders.Work, ["diff", "--cached", "--binary", "--no-renames", "--no-ext-diff", "--no-textconv", "--output", folders.PatchFile, "--", ".", .. excludes], network: false, 200, cancellationToken);
        if (diff.ExitCode != 0 || !File.Exists(folders.PatchFile) || new FileInfo(folders.PatchFile).Length is 0 or > MaxPatchBytes)
        {
            return null;
        }
        // The digest is of the bytes of the file that was applied and checked, not of the text decoded from it
        var bytes = await File.ReadAllBytesAsync(folders.PatchFile, cancellationToken);
        return (System.Text.Encoding.UTF8.GetString(bytes), bytes.LongLength, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
    }

    // The instructions of the pull request are for whoever reads it, not for the agent: they are taken out of the folder it works in. So is every link
    // (to /proc/self/environ, where the token is, or to anything of the host): what the pull request or its install step made as a link is not something
    // the agent may read through.
    // Returns the paths (from the folder) of what it removed, so that the removal of a link the pull request tracks is not counted as the agent's change.
    // node_modules is walked too: an install step may leave a link there (and a .git, an instruction file or a folder of instructions is removed there as anywhere).
    private static List<string> RemoveInstructions(string work)
    {
        var removed = new List<string>();
        var pending = new Stack<string>([work]);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                var name = Path.GetFileName(entry);
                var info = new FileInfo(entry);
                if (info.LinkTarget is not null)
                {
                    // Every link goes, not only the ones that seem to leave the folder: where a chain of links ends cannot be judged from the names (y -> . and
                    // x -> y/.. looks like it stays inside and is the parent of the folder), and the agent has no use for a link: the steps are run by the executor
                    PullRequestWorkspace.DeleteEntry(entry);
                    var relative = Path.GetRelativePath(work, entry);
                    if (!relative.Split(Path.DirectorySeparatorChar).Contains("node_modules", StringComparer.Ordinal))
                    {
                        removed.Add(relative);
                    }
                }
                else if (Directory.Exists(entry))
                {
                    if (InstructionFolders.Contains(name, StringComparer.Ordinal) || name == ".git")
                    {
                        // A .git that the code of the pull request wrote is a repository the agent's own git would read (and run what it names)
                        PullRequestWorkspace.DeleteEntry(entry);
                    }
                    else
                    {
                        pending.Push(entry);
                    }
                }
                else if (InstructionFiles.Contains(name, StringComparer.Ordinal) || name == ".git")
                {
                    PullRequestWorkspace.DeleteEntry(entry);
                }
            }
        }
        return removed;
    }

    private static void AddPatch(TaskSpec task, RunTrace trace, (string Text, long Bytes, string Digest) written, int files)
    {
        // Masked before it is cut into pieces of the trace: a token that falls on the edge of two pieces would not be found in either
        var patch = trace.Masked(written.Text);
        var chunks = Math.Min(MaxPatchChunks, (patch.Length + PatchChunk - 1) / PatchChunk);
        // What identifies the whole patch, shown or not: the next step (applying it) binds an approval to these bytes
        trace.Add(RunEvents.TaskPatch, task.Id, $"patch: {written.Bytes.ToString(CultureInfo.InvariantCulture)} bytes, sha256 {written.Digest}");
        if (patch.Length > chunks * PatchChunk)
        {
            // What is shown is not the whole patch: it says so (the whole patch was applied and checked)
            trace.Add(RunEvents.TaskPatch, task.Id, $"patch cut: {(chunks * PatchChunk).ToString(CultureInfo.InvariantCulture)} of {patch.Length.ToString(CultureInfo.InvariantCulture)} characters are shown; the whole patch was applied and checked");
        }
        for (var index = 0; index < chunks; index++)
        {
            var part = patch.Substring(index * PatchChunk, Math.Min(PatchChunk, patch.Length - (index * PatchChunk)));
            trace.Add(RunEvents.TaskPatch, task.Id, $"patch {(index + 1).ToString(CultureInfo.InvariantCulture)}/{chunks.ToString(CultureInfo.InvariantCulture)} ({files.ToString(CultureInfo.InvariantCulture)} file(s))\n{part}");
        }
    }

    private List<string> AgentArguments(TaskSpec task, RunFolders folders, string container, StepsOutcome before)
    {
        // A marker that is different for each run, so that the output (written by whoever wrote the pull request) cannot guess it and end the data early
        var marker = $"OUTPUT-{Guid.NewGuid():N}";
        var output = before.FailedOutput ?? string.Empty;
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
            // The built-in tools that exist at all are these four (an allow-list that a new version of the tool cannot widen), approved without a prompt, and the rest named below is denied twice
            "--tools", "Read,Edit,Glob,Grep",
            "--allowedTools", "Read", "Edit", "Glob", "Grep",
            "--disallowedTools", "Bash", "WebFetch", "WebSearch", .. ForbiddenReads, .. ForbiddenEdits,
        ]);
        return arguments;
    }

    private static string Tail(string text) => text.Length > 1500 ? text[^1500..] : text;
}
