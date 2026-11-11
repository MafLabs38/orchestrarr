using System.Globalization;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor;

/// <summary>How the steps of a profile ended: a result and, for a failure, which step and the end of its output (what an agent is shown).</summary>
internal sealed record StepsOutcome(TaskResult Result, string Reason, string? FailedStep = null, string? FailedOutput = null);

/// <summary>
/// What the tasks that work on a pull request share: fetching its head into a fresh folder (checked against the approved commit), and running the
/// steps of a profile in a container with no credential, no root and bounded resources. The folder is made and removed by the caller.
/// </summary>
internal sealed class PullRequestWorkspace(ValidationOptions options, IProcessRunner processes)
{
    // The exit codes with which docker itself says that it could not run the container, as opposed to the command inside it failing
    private const int DockerFailure = 125;

    private const int DockerNotRunnable = 126;

    private const int DockerNotFound = 127;

    public ValidationProfile? ProfileFor(string repository) =>
        options.Profiles.FirstOrDefault(candidate => candidate.Repositories.Any(pattern => TaskShape.Matches(pattern, repository)) && candidate.Steps.Count > 0 && candidate.Image.Length > 0);

    public string FolderFor(TaskSpec task) => Path.Combine(Path.GetFullPath(options.WorkRoot), task.Id.ToString("N"));

    public Dictionary<string, string> GitEnvironment()
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

    public TimeSpan StepTimeout() => TimeSpan.FromMinutes(Math.Max(1, options.StepTimeoutMinutes));

    /// <summary>Fetches the pull request into <paramref name="folder"/>. A refusal reason, or null when the head is the approved commit and is checked out.</summary>
    public async Task<string?> FetchAsync(TaskSpec task, RunTrace trace, string folder, CancellationToken cancellationToken)
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
                return "the pull request could not be fetched";
            }
        }

        var head = await processes.RunAsync("git", ["-C", folder, "rev-parse", "FETCH_HEAD"], null, environment, StepTimeout(), options.MaxOutputCharacters, cancellationToken);
        var fetched = head.OutputTail.Trim();
        if (head.ExitCode != 0 || !string.Equals(fetched, task.HeadSha, StringComparison.OrdinalIgnoreCase))
        {
            trace.Add(RunEvents.TaskStep, task.Id, $"fetch: the head of the pull request is {Short(fetched)}, the approval was for {Short(task.HeadSha)}");
            return "the head of the pull request is not the approved commit";
        }

        var checkout = await processes.RunAsync("git", ["-C", folder, "-c", "advice.detachedHead=false", "checkout", "--quiet", "FETCH_HEAD"], null, environment, StepTimeout(), options.MaxOutputCharacters, cancellationToken);
        if (checkout.ExitCode != 0)
        {
            trace.Add(RunEvents.TaskStep, task.Id, $"checkout: exit {checkout.ExitCode}\n{checkout.OutputTail}");
            return "the commit could not be checked out";
        }
        trace.Add(RunEvents.TaskStep, task.Id, $"fetch: the head of the pull request is the approved commit {Short(task.HeadSha)}");
        return null;
    }

    /// <summary>Runs the steps of the profile in order; the first that fails ends them. <paramref name="label"/> names the pass in the trace (for example "after the repair").</summary>
    public async Task<StepsOutcome> RunStepsAsync(TaskSpec task, RunTrace trace, ValidationProfile profile, string folder, string label, CancellationToken cancellationToken)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var index = 0; index < profile.Steps.Count; index++)
        {
            var step = profile.Steps[index];
            var title = label.Length == 0 ? step.Name : $"{step.Name} ({label})";
            var container = $"orchestrarr-{task.Id:N}-{label.Length}-{index}";
            var started = clock.Elapsed;
            var result = await processes.RunAsync("docker", DockerArguments(profile, step, container, folder), null, new Dictionary<string, string>(), StepTimeout(), options.MaxOutputCharacters, cancellationToken);
            var seconds = (clock.Elapsed - started).TotalSeconds;
            if (result.TimedOut)
            {
                // Stopping the client does not stop the container
                await Kill(container);
                trace.Add(RunEvents.TaskStep, task.Id, $"{title}: stopped after {options.StepTimeoutMinutes} min\n{result.OutputTail}");
                return new StepsOutcome(TaskResult.Failed, $"{step.Name} took more than {options.StepTimeoutMinutes} min", step.Name, result.OutputTail);
            }
            if (result.ExitCode is DockerFailure or DockerNotRunnable or DockerNotFound)
            {
                trace.Add(RunEvents.TaskStep, task.Id, $"{title}: the container did not start (exit {result.ExitCode})\n{result.OutputTail}");
                return new StepsOutcome(TaskResult.Refused, "the container could not be started");
            }
            if (result.ExitCode != 0)
            {
                trace.Add(RunEvents.TaskStep, task.Id, $"{title}: failed, exit {result.ExitCode}, {seconds:F0} s\n{result.OutputTail}");
                return new StepsOutcome(TaskResult.Failed, $"{step.Name} failed (exit {result.ExitCode})", step.Name, result.OutputTail);
            }
            trace.Add(RunEvents.TaskStep, task.Id, $"{title}: passed, {seconds:F0} s\n{result.OutputTail}");
        }
        return new StepsOutcome(TaskResult.Passed, $"{profile.Steps.Count} steps passed");
    }

    public async Task Kill(string container) =>
        await processes.RunAsync("docker", ["kill", container], null, new Dictionary<string, string>(), TimeSpan.FromSeconds(30), 200, CancellationToken.None);

    public static void Remove(string folder)
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

    public static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

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
}
