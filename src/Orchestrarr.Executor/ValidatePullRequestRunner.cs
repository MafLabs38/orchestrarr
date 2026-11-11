using System.Globalization;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor;

/// <summary>
/// Validates a pull request for real: it fetches the head of the pull request, checks that it is the commit that was approved (the fact comes from
/// the forge, not from the task), and runs the steps of the repository's profile in a container. It changes nothing in the repository and keeps no
/// credential in the container. A step that fails is a result (<see cref="TaskResult.Failed"/>); what keeps the run from happening at all
/// (no profile, no fetch, a commit that moved, a container that does not start) is a refusal.
/// </summary>
public sealed class ValidatePullRequestRunner(ValidationOptions options, IProcessRunner processes) : ITaskRunner
{
    // The exit codes with which docker itself says that it could not run the container, as opposed to the command inside it failing
    private const int DockerFailure = 125;

    private const int DockerNotRunnable = 126;

    private const int DockerNotFound = 127;

    public TaskKind Kind => TaskKind.ValidatePullRequest;

    // The container is thrown away, nothing is written to the repository: this runner may run on an executor that does not ask for the code (development)
    public bool ChangesNothing => true;

    public async ValueTask<TaskOutcome> RunAsync(TaskSpec task, RunTrace trace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(trace);
        var profile = options.Profiles.FirstOrDefault(candidate => candidate.Repositories.Any(pattern => TaskShape.Matches(pattern, task.Repository)));
        if (profile is null || profile.Steps.Count == 0 || profile.Image.Length == 0)
        {
            return Refuse(task, trace, "no validation profile for this repository");
        }

        trace.Add(RunEvents.TaskStarted, task.Id, $"validating {task.Repository}#{task.PullRequest} at {Short(task.HeadSha)} in {profile.Image}");
        var folder = Path.Combine(Path.GetFullPath(options.WorkRoot), task.Id.ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            if (await FetchAsync(task, trace, folder, cancellationToken) is { } refusal)
            {
                return refusal;
            }
            return await RunStepsAsync(task, trace, profile, folder, cancellationToken);
        }
        finally
        {
            Remove(folder);
        }
    }

    private async Task<TaskOutcome?> FetchAsync(TaskSpec task, RunTrace trace, string folder, CancellationToken cancellationToken)
    {
        var environment = GitEnvironment();
        var url = string.Format(CultureInfo.InvariantCulture, options.RepositoryUrl, task.Repository);
        var pullRef = $"refs/pull/{task.PullRequest.ToString(CultureInfo.InvariantCulture)}/head";
        string[][] commands =
        [
            ["init", "--quiet", folder],
            ["-C", folder, "fetch", "--quiet", "--depth", "1", url, pullRef],
        ];
        foreach (var arguments in commands)
        {
            var result = await processes.RunAsync("git", arguments, null, environment, StepTimeout(), options.MaxOutputCharacters, cancellationToken);
            if (result.ExitCode != 0)
            {
                trace.Add(RunEvents.TaskStep, task.Id, $"fetch: exit {result.ExitCode}\n{result.OutputTail}");
                return Refuse(task, trace, "the pull request could not be fetched");
            }
        }

        var head = await processes.RunAsync("git", ["-C", folder, "rev-parse", "FETCH_HEAD"], null, environment, StepTimeout(), options.MaxOutputCharacters, cancellationToken);
        var fetched = head.OutputTail.Trim();
        if (head.ExitCode != 0 || !string.Equals(fetched, task.HeadSha, StringComparison.OrdinalIgnoreCase))
        {
            trace.Add(RunEvents.TaskStep, task.Id, $"fetch: the head of the pull request is {Short(fetched)}, the approval was for {Short(task.HeadSha)}");
            return Refuse(task, trace, "the head of the pull request is not the approved commit");
        }

        var checkout = await processes.RunAsync("git", ["-C", folder, "-c", "advice.detachedHead=false", "checkout", "--quiet", "FETCH_HEAD"], null, environment, StepTimeout(), options.MaxOutputCharacters, cancellationToken);
        if (checkout.ExitCode != 0)
        {
            trace.Add(RunEvents.TaskStep, task.Id, $"checkout: exit {checkout.ExitCode}\n{checkout.OutputTail}");
            return Refuse(task, trace, "the commit could not be checked out");
        }
        trace.Add(RunEvents.TaskStep, task.Id, $"fetch: the head of the pull request is the approved commit {Short(task.HeadSha)}");
        return null;
    }

    private async Task<TaskOutcome> RunStepsAsync(TaskSpec task, RunTrace trace, ValidationProfile profile, string folder, CancellationToken cancellationToken)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var index = 0; index < profile.Steps.Count; index++)
        {
            var step = profile.Steps[index];
            var container = $"orchestrarr-{task.Id:N}-{index}";
            var started = clock.Elapsed;
            var result = await processes.RunAsync("docker", DockerArguments(profile, step, container, folder), null, new Dictionary<string, string>(), StepTimeout(), options.MaxOutputCharacters, cancellationToken);
            var seconds = (clock.Elapsed - started).TotalSeconds;
            if (result.TimedOut)
            {
                // Stopping the client does not stop the container
                await processes.RunAsync("docker", ["kill", container], null, new Dictionary<string, string>(), TimeSpan.FromSeconds(30), 200, CancellationToken.None);
                trace.Add(RunEvents.TaskStep, task.Id, $"{step.Name}: stopped after {options.StepTimeoutMinutes} min\n{result.OutputTail}");
                return Finish(task, trace, TaskResult.Failed, $"{step.Name} took more than {options.StepTimeoutMinutes} min");
            }
            if (result.ExitCode is DockerFailure or DockerNotRunnable or DockerNotFound)
            {
                trace.Add(RunEvents.TaskStep, task.Id, $"{step.Name}: the container did not start (exit {result.ExitCode})\n{result.OutputTail}");
                return Refuse(task, trace, "the container could not be started");
            }
            if (result.ExitCode != 0)
            {
                trace.Add(RunEvents.TaskStep, task.Id, $"{step.Name}: failed, exit {result.ExitCode}, {seconds:F0} s\n{result.OutputTail}");
                return Finish(task, trace, TaskResult.Failed, $"{step.Name} failed (exit {result.ExitCode})");
            }
            trace.Add(RunEvents.TaskStep, task.Id, $"{step.Name}: passed, {seconds:F0} s\n{result.OutputTail}");
        }
        return Finish(task, trace, TaskResult.Passed, $"{profile.Steps.Count} steps passed");
    }

    private List<string> DockerArguments(ValidationProfile profile, ValidationStep step, string container, string folder)
    {
        List<string> arguments =
        [
            "run", "--rm", "--name", container,
            "--cpus", options.Cpus.ToString(CultureInfo.InvariantCulture),
            "--memory", $"{options.MemoryMegabytes.ToString(CultureInfo.InvariantCulture)}m",
            "--pids-limit", "512",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--user", options.ContainerUser,
            "--tmpfs", "/tmp:rw,exec,size=512m",
            "-e", "HOME=/tmp",
            "-e", "CI=true",
        ];
        if (!step.Network)
        {
            arguments.AddRange(["--network", "none"]);
        }
        arguments.AddRange(["-v", $"{folder}:/work", "-w", "/work", profile.Image, "sh", "-c", step.Command]);
        return arguments;
    }

    private Dictionary<string, string> GitEnvironment()
    {
        var environment = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };
        if (options.GitTokenFile.Length > 0)
        {
            // The token travels in the environment of the git process (not in its arguments, which any process can list) and nowhere else
            var token = File.ReadAllText(options.GitTokenFile).Trim();
            var header = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"x-access-token:{token}"));
            environment["GIT_CONFIG_COUNT"] = "1";
            environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            environment["GIT_CONFIG_VALUE_0"] = $"Authorization: Basic {header}";
        }
        return environment;
    }

    private TimeSpan StepTimeout() => TimeSpan.FromMinutes(Math.Max(1, options.StepTimeoutMinutes));

    private static TaskOutcome Refuse(TaskSpec task, RunTrace trace, string reason)
    {
        trace.Add(RunEvents.TaskRefused, task.Id, reason);
        return new TaskOutcome(task.Id, TaskResult.Refused, reason);
    }

    private static TaskOutcome Finish(TaskSpec task, RunTrace trace, TaskResult result, string reason)
    {
        trace.Add(result == TaskResult.Passed ? RunEvents.TaskPassed : RunEvents.TaskFailed, task.Id, reason);
        return new TaskOutcome(task.Id, result, reason);
    }

    private static void Remove(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
            // A folder left behind is a nuisance, not a reason to change the result of the run
        }
    }

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;
}
