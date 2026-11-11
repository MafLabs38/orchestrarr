using System.Globalization;
using System.Text.Json;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor;

/// <summary>
/// Tries to repair a pull request, in the workshop and on paper: it validates the pull request first (nothing to repair when it passes), then lets an
/// agent edit the code in a container, refuses a change to a protected file, validates again, and puts the patch in the trace. The patch is the
/// result: nothing is pushed to the repository, so the runner changes nothing outside its sandbox. The agent's words are in the trace; they are not
/// believed: the steps are run again by the executor, and only their result counts.
/// </summary>
public sealed class RepairPullRequestRunner(ValidationOptions validation, RepairOptions repair, IProcessRunner processes) : ITaskRunner
{
    private const int PatchChunk = 3500;

    private const int MaxPatchChunks = 8;

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

        trace.Add(RunEvents.TaskStarted, task.Id, $"repairing {task.Repository}#{task.PullRequest} at {PullRequestWorkspace.Short(task.HeadSha)}: validate, then let an agent try, then validate again");
        var folder = _workspace.FolderFor(task);
        try
        {
            Directory.CreateDirectory(folder);
            if (await _workspace.FetchAsync(task, trace, folder, cancellationToken) is { } reason)
            {
                return ValidatePullRequestRunner.Refuse(task, trace, reason);
            }

            var before = await _workspace.RunStepsAsync(task, trace, profile, folder, "before", cancellationToken);
            if (before.Result == TaskResult.Refused)
            {
                return ValidatePullRequestRunner.Refuse(task, trace, before.Reason);
            }
            if (before.Result == TaskResult.Passed)
            {
                return ValidatePullRequestRunner.Finish(task, trace, TaskResult.Passed, "nothing to repair: the validation passes");
            }

            return await TryRepairAsync(task, trace, profile, folder, before, cancellationToken);
        }
        finally
        {
            PullRequestWorkspace.Remove(folder);
        }
    }

    private async Task<TaskOutcome> TryRepairAsync(TaskSpec task, RunTrace trace, ValidationProfile profile, string folder, StepsOutcome before, CancellationToken cancellationToken)
    {
        var token = (await File.ReadAllTextAsync(repair.TokenFile, cancellationToken)).Trim();
        var container = $"orchestrarr-agent-{task.Id:N}";
        var run = await processes.RunAsync("docker", AgentArguments(task, profile, folder, container, before), null, new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = token }, TimeSpan.FromMinutes(Math.Max(1, repair.TimeoutMinutes)), 20000, cancellationToken);
        if (run.TimedOut)
        {
            await _workspace.Kill(container);
            trace.Add(RunEvents.TaskStep, task.Id, $"agent: stopped after {repair.TimeoutMinutes} min");
            return ValidatePullRequestRunner.Finish(task, trace, TaskResult.Failed, $"the agent took more than {repair.TimeoutMinutes} min");
        }
        if (run.ExitCode != 0)
        {
            // Never what the agent printed in the reason: it can carry anything. The trace keeps the end of it, cut.
            trace.Add(RunEvents.TaskStep, task.Id, $"agent: exit {run.ExitCode}\n{Tail(run.OutputTail)}");
            return run.ExitCode is 125 or 126 or 127
                ? ValidatePullRequestRunner.Refuse(task, trace, "the agent's container could not be started")
                : ValidatePullRequestRunner.Finish(task, trace, TaskResult.Failed, "the agent ended with an error");
        }
        trace.Add(RunEvents.TaskStep, task.Id, "agent: " + Summary(run.OutputTail));

        var git = _workspace.GitEnvironment();
        var gitTimeout = _workspace.StepTimeout();
        await processes.RunAsync("git", ["-C", folder, "add", "-A"], null, git, gitTimeout, 200, cancellationToken);
        var names = await processes.RunAsync("git", ["-C", folder, "diff", "--cached", "--name-only"], null, git, gitTimeout, 4000, cancellationToken);
        var paths = names.OutputTail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (paths.Length == 0)
        {
            return ValidatePullRequestRunner.Finish(task, trace, TaskResult.Failed, "the agent made no change");
        }
        if (paths.FirstOrDefault(path => repair.ProtectedPaths.Any(pattern => TaskShape.Matches(pattern, path))) is { } protectedPath)
        {
            trace.Add(RunEvents.TaskStep, task.Id, $"agent: it changed a protected file, {protectedPath}: the repair is refused");
            return ValidatePullRequestRunner.Finish(task, trace, TaskResult.Failed, "the agent changed a protected file");
        }

        // The executor checks the repair itself: the agent saying that it works is not a fact
        var after = await _workspace.RunStepsAsync(task, trace, profile, folder, "after", cancellationToken);
        if (after.Result != TaskResult.Passed)
        {
            return after.Result == TaskResult.Refused
                ? ValidatePullRequestRunner.Refuse(task, trace, after.Reason)
                : ValidatePullRequestRunner.Finish(task, trace, TaskResult.Failed, "the agent's change does not make the validation pass: " + after.Reason);
        }

        var patch = await processes.RunAsync("git", ["-C", folder, "diff", "--cached"], null, git, gitTimeout, PatchChunk * MaxPatchChunks, cancellationToken);
        AddPatch(task, trace, patch.OutputTail, paths.Length);
        return ValidatePullRequestRunner.Finish(task, trace, TaskResult.Passed, $"repaired: {paths.Length.ToString(CultureInfo.InvariantCulture)} file(s) changed, the validation passes again (patch in the trace, nothing pushed)");
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

    private List<string> AgentArguments(TaskSpec task, ValidationProfile profile, string folder, string container, StepsOutcome before)
    {
        var prompt = repair.Prompt
            .Replace("{repository}", task.Repository, StringComparison.Ordinal)
            .Replace("{number}", task.PullRequest.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{step}", before.FailedStep ?? "validation", StringComparison.Ordinal)
            .Replace("{protected}", string.Join(", ", repair.ProtectedPaths), StringComparison.Ordinal)
            .Replace("{output}", before.FailedOutput ?? string.Empty, StringComparison.Ordinal);
        List<string> arguments =
        [
            "run", "--rm", "--name", container,
            "--cpus", repair.Cpus.ToString(CultureInfo.InvariantCulture),
            "--memory", $"{repair.MemoryMegabytes.ToString(CultureInfo.InvariantCulture)}m",
            "--pids-limit", "512",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--user", validation.ContainerUser,
            "--tmpfs", "/tmp:rw,exec,size=512m",
            "-e", "HOME=/tmp",
            "-e", "CLAUDE_CODE_OAUTH_TOKEN",
            "-v", $"{folder}:/work", "-w", "/work", repair.Image,
            "claude", "-p", prompt,
            "--model", repair.Model,
            "--permission-mode", "acceptEdits",
            "--max-turns", repair.MaxTurns.ToString(CultureInfo.InvariantCulture),
            "--output-format", "json",
            "--allowedTools", "Read", "Edit", "Glob", "Grep",
        ];
        // The agent may run the steps of the profile, exactly as they are written, and nothing else
        arguments.AddRange(profile.Steps.Select(step => $"Bash({step.Command})"));
        return arguments;
    }

    private static string Summary(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var turns = root.TryGetProperty("num_turns", out var turnCount) ? turnCount.ToString() : "?";
            var result = root.TryGetProperty("result", out var text) ? text.GetString() ?? string.Empty : string.Empty;
            return $"{turns} turns\n{result}";
        }
        catch (JsonException)
        {
            return Tail(json);
        }
    }

    private static string Tail(string text) => text.Length > 1500 ? text[^1500..] : text;
}
