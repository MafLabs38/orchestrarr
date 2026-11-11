using System.Globalization;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor;

/// <summary>How the steps of a profile ended: a result and, for a failure, which step and the end of its output (what an agent is shown).</summary>
internal sealed record StepsOutcome(TaskResult Result, string Reason, string? FailedStep = null, string? FailedOutput = null);

/// <summary>
/// The folders of one run. <c>Work</c> is the only one a container sees. <c>GitDir</c> is outside it, so that nothing a container writes (the code of a
/// pull request, an agent) can change what git does on the host: git reads its configuration, hooks and filters from the git directory.
/// </summary>
internal sealed record RunFolders(string Root, string GitDir, string Work)
{
    /// <summary>A second work tree: the approved commit with exactly the patch applied, where a repair is checked.</summary>
    public string WorkAfter => Path.Combine(Root, "work-after");

    /// <summary>Where the patch is kept, outside every mount.</summary>
    public string PatchFile => Path.Combine(Root, "repair.patch");
}

/// <summary>
/// What the tasks that work on a pull request share: fetching its head (checked against the approved commit), and running the steps of a profile in a
/// container with no credential, no root and bounded resources. The folder is made and removed by the caller. Git runs on the host only with the git
/// directory outside the mounted folder and with its hooks, monitors and external programs off.
/// </summary>
internal sealed class PullRequestWorkspace(ValidationOptions options, IProcessRunner processes)
{
    /// <summary>The folder of the run, as the container sees it.</summary>
    public const string ContainerWork = "/work";

    // The exit codes with which docker itself says that it could not run the container, as opposed to the command inside it failing
    public static bool DockerCouldNotRun(int exitCode) => exitCode is 125 or 126 or 127;

    public ValidationProfile? ProfileFor(string repository) =>
        options.Profiles.FirstOrDefault(candidate => candidate.Repositories.Any(pattern => TaskShape.Matches(pattern, repository)) && candidate.Steps.Count > 0 && candidate.Image.Length > 0);

    public RunFolders FoldersFor(TaskSpec task)
    {
        var root = Path.Combine(Path.GetFullPath(options.WorkRoot), task.Id.ToString("N"));
        return new RunFolders(root, Path.Combine(root, "git"), Path.Combine(root, "work"));
    }

    /// <summary>The secrets that must never reach the trace: the token to fetch with.</summary>
    public string? GitToken() => options.GitTokenFile.Length > 0 ? File.ReadAllText(options.GitTokenFile).Trim() : null;

    // The environment of every git call of the host: no system or user configuration, no prompt. The token only goes to the call that fetches.
    private static Dictionary<string, string> GitEnvironment() => new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["GIT_CONFIG_GLOBAL"] = "/dev/null",
        ["GIT_ATTR_NOSYSTEM"] = "1",
    };

    private Dictionary<string, string> FetchEnvironment()
    {
        var environment = GitEnvironment();
        if (GitToken() is { } token)
        {
            // The token travels in the environment of the git process (not in its arguments, which any process can list) and nowhere else
            var header = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"x-access-token:{token}"));
            environment["GIT_CONFIG_COUNT"] = "1";
            environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            environment["GIT_CONFIG_VALUE_0"] = $"Authorization: Basic {header}";
        }
        return environment;
    }

    /// <summary>
    /// Runs git on the host against a git directory and a work tree given explicitly (a <c>.git</c> in the work tree is never read), with the programs a
    /// repository could name switched off. <paramref name="network"/> is true only for the fetch, the one call that gets the token.
    /// </summary>
    public Task<ProcessResult> GitAsync(RunFolders folders, string workTree, IReadOnlyList<string> arguments, bool network, int maxOutputCharacters, CancellationToken cancellationToken)
    {
        List<string> all =
        [
            "--git-dir", folders.GitDir, "--work-tree", workTree,
            "-c", "core.fsmonitor=false", "-c", "core.hooksPath=/dev/null", "-c", "core.quotePath=false", "-c", "http.followRedirects=false", "-c", "advice.detachedHead=false",
            .. arguments,
        ];
        return processes.RunAsync("git", all, null, network ? FetchEnvironment() : GitEnvironment(), StepTimeout(), maxOutputCharacters, cancellationToken);
    }

    public TimeSpan StepTimeout() => TimeSpan.FromMinutes(Math.Max(1, options.StepTimeoutMinutes));

    /// <summary>Fetches the pull request into the run's work tree. A refusal reason, or null when the head is the approved commit and is checked out.</summary>
    public async Task<string?> FetchAsync(TaskSpec task, RunTrace trace, RunFolders folders, CancellationToken cancellationToken)
    {
        var url = string.Format(CultureInfo.InvariantCulture, options.RepositoryUrl, task.Repository);
        var pullRef = $"refs/pull/{task.PullRequest.ToString(CultureInfo.InvariantCulture)}/head";
        Directory.CreateDirectory(folders.Work);
        // The git directory is created apart from the work tree, with no template (no hook); the pointer file that git leaves in the work tree is removed
        var init = await processes.RunAsync("git", ["init", "--quiet", "--template=", "--separate-git-dir", folders.GitDir, folders.Work], null, GitEnvironment(), StepTimeout(), options.MaxOutputCharacters, cancellationToken);
        if (init.ExitCode != 0)
        {
            trace.Add(RunEvents.TaskStep, task.Id, $"fetch: exit {init.ExitCode}\n{init.OutputTail}");
            return "the pull request could not be fetched";
        }
        RemoveGitEntry(folders.Work);

        var fetch = await GitAsync(folders, folders.Work, ["fetch", "--quiet", "--depth", "1", url, pullRef], network: true, options.MaxOutputCharacters, cancellationToken);
        if (fetch.ExitCode != 0)
        {
            trace.Add(RunEvents.TaskStep, task.Id, $"fetch: exit {fetch.ExitCode}\n{fetch.OutputTail}");
            return "the pull request could not be fetched";
        }

        var head = await GitAsync(folders, folders.Work, ["rev-parse", "FETCH_HEAD"], network: false, options.MaxOutputCharacters, cancellationToken);
        var fetched = head.OutputTail.Trim();
        if (head.ExitCode != 0 || !string.Equals(fetched, task.HeadSha, StringComparison.OrdinalIgnoreCase))
        {
            trace.Add(RunEvents.TaskStep, task.Id, $"fetch: the head of the pull request is {Short(fetched)}, the approval was for {Short(task.HeadSha)}");
            return "the head of the pull request is not the approved commit";
        }

        var checkout = await GitAsync(folders, folders.Work, ["checkout", "--quiet", "FETCH_HEAD"], network: false, options.MaxOutputCharacters, cancellationToken);
        if (checkout.ExitCode != 0)
        {
            trace.Add(RunEvents.TaskStep, task.Id, $"checkout: exit {checkout.ExitCode}\n{checkout.OutputTail}");
            return "the commit could not be checked out";
        }
        trace.Add(RunEvents.TaskStep, task.Id, $"fetch: the head of the pull request is the approved commit {Short(task.HeadSha)}");
        return null;
    }

    /// <summary>Runs the steps of the profile in order, in <paramref name="work"/>; the first that fails ends them. <paramref name="label"/> names the pass in the trace and in the container.</summary>
    public async Task<StepsOutcome> RunStepsAsync(TaskSpec task, RunTrace trace, ValidationProfile profile, string work, string label, CancellationToken cancellationToken)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var pass = label.Length == 0 ? "run" : label;
        for (var index = 0; index < profile.Steps.Count; index++)
        {
            var step = profile.Steps[index];
            var title = label.Length == 0 ? step.Name : $"{step.Name} ({label})";
            var container = $"orchestrarr-{task.Id:N}-{pass}-{index.ToString(CultureInfo.InvariantCulture)}";
            var started = clock.Elapsed;
            List<string> arguments = [.. SandboxArguments(container, work, options.Cpus, options.MemoryMegabytes, step.Network), profile.Image, "sh", "-c", step.Command];
            var result = await processes.RunAsync("docker", arguments, null, new Dictionary<string, string>(), StepTimeout(), options.MaxOutputCharacters, cancellationToken);
            var seconds = (clock.Elapsed - started).TotalSeconds;
            if (result.TimedOut)
            {
                // Stopping the client does not stop the container
                await KillAsync(container);
                trace.Add(RunEvents.TaskStep, task.Id, $"{title}: stopped after {options.StepTimeoutMinutes} min\n{result.OutputTail}");
                return new StepsOutcome(TaskResult.Failed, $"{step.Name} took more than {options.StepTimeoutMinutes} min", step.Name, result.OutputTail);
            }
            if (DockerCouldNotRun(result.ExitCode))
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

    /// <summary>
    /// The flags that make a container a sandbox, in one place for the steps and for the agent: bounded CPU, memory and processes, no capability, no
    /// privilege gained, not root, a small temporary folder, the network only where asked, and nothing of the host but the work folder.
    /// </summary>
    public List<string> SandboxArguments(string container, string work, double cpus, int memoryMegabytes, bool network, IEnumerable<string>? environment = null)
    {
        List<string> arguments =
        [
            "run", "--rm", "--name", container,
            "--cpus", cpus.ToString(CultureInfo.InvariantCulture),
            "--memory", $"{memoryMegabytes.ToString(CultureInfo.InvariantCulture)}m",
            "--pids-limit", options.PidsLimit.ToString(CultureInfo.InvariantCulture),
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--user", options.ContainerUser,
            "--tmpfs", $"/tmp:rw,exec,size={options.TmpfsMegabytes.ToString(CultureInfo.InvariantCulture)}m",
            "-e", "HOME=/tmp",
        ];
        foreach (var variable in environment ?? ["CI=true"])
        {
            arguments.AddRange(["-e", variable]);
        }
        if (!network)
        {
            arguments.AddRange(["--network", "none"]);
        }
        arguments.AddRange(["-v", $"{work}:{ContainerWork}", "-w", ContainerWork]);
        return arguments;
    }

    /// <summary>
    /// A second work tree with the approved commit and exactly the patch applied: a repair is checked there, not in the folder that the steps and the agent
    /// left behind (ignored files, caches). A refusal reason, or null.
    /// </summary>
    public async Task<string?> CleanCopyWithPatchAsync(RunFolders folders, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folders.WorkAfter);
        var checkout = await GitAsync(folders, folders.WorkAfter, ["checkout", "--quiet", "--force", "FETCH_HEAD", "--", "."], network: false, options.MaxOutputCharacters, cancellationToken);
        if (checkout.ExitCode != 0)
        {
            return "the approved commit could not be checked out again";
        }
        // Outside any repository: git applies the patch to the files of the folder, and refuses a path outside it or beyond a link
        var apply = await processes.RunAsync("git", ["-c", "core.fsmonitor=false", "apply", "--whitespace=nowarn", folders.PatchFile], folders.WorkAfter, GitEnvironment(), StepTimeout(), options.MaxOutputCharacters, cancellationToken);
        return apply.ExitCode == 0 ? null : "the patch does not apply to the approved commit";
    }

    public async Task KillAsync(string container) =>
        await processes.RunAsync("docker", ["kill", container], null, new Dictionary<string, string>(), TimeSpan.FromSeconds(30), 200, CancellationToken.None);

    /// <summary>A <c>.git</c> in a work tree is nothing the host trusts: it is removed before a container is started and before the host reads the tree.</summary>
    public static void RemoveGitEntry(string work)
    {
        var entry = Path.Combine(work, ".git");
        if (File.Exists(entry))
        {
            File.Delete(entry);
        }
        else if (Directory.Exists(entry))
        {
            Directory.Delete(entry, recursive: true);
        }
    }

    /// <summary>
    /// Removes the folders of a run. False when it could not (files made by a container's user): a folder left behind is not a reason to change the result
    /// of the run, and the caller says so in the trace so that folders do not pile up unseen.
    /// </summary>
    public static bool Remove(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
            return true;
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes the run's folders and tells the trace when it could not.</summary>
    public static void Clean(RunFolders folders, TaskSpec task, RunTrace trace)
    {
        if (!Remove(folders.Root))
        {
            trace.Add(RunEvents.TaskStep, task.Id, $"cleanup: the folder {folders.Root} could not be removed");
        }
    }

    public static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;
}
