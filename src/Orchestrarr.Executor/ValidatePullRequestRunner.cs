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
    private readonly PullRequestWorkspace _workspace = new(options, processes);

    public TaskKind Kind => TaskKind.ValidatePullRequest;

    // The container is thrown away, nothing is written to the repository: this runner may run on an executor that does not ask for the code (development)
    public bool ChangesNothing => true;

    public async ValueTask<TaskOutcome> RunAsync(TaskSpec task, RunTrace trace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(trace);
        if (_workspace.ProfileFor(task.Repository) is not { } profile)
        {
            return Refuse(task, trace, "no validation profile for this repository");
        }

        trace.Add(RunEvents.TaskStarted, task.Id, $"validating {task.Repository}#{task.PullRequest} at {PullRequestWorkspace.Short(task.HeadSha)} in {profile.Image}");
        var folder = _workspace.FolderFor(task);
        try
        {
            Directory.CreateDirectory(folder);
            if (await _workspace.FetchAsync(task, trace, folder, cancellationToken) is { } reason)
            {
                return Refuse(task, trace, reason);
            }
            var steps = await _workspace.RunStepsAsync(task, trace, profile, folder, string.Empty, cancellationToken);
            return steps.Result == TaskResult.Refused ? Refuse(task, trace, steps.Reason) : Finish(task, trace, steps.Result, steps.Reason);
        }
        finally
        {
            PullRequestWorkspace.Remove(folder);
        }
    }

    internal static TaskOutcome Refuse(TaskSpec task, RunTrace trace, string reason)
    {
        trace.Add(RunEvents.TaskRefused, task.Id, reason);
        return new TaskOutcome(task.Id, TaskResult.Refused, reason);
    }

    internal static TaskOutcome Finish(TaskSpec task, RunTrace trace, TaskResult result, string reason)
    {
        trace.Add(result == TaskResult.Passed ? RunEvents.TaskPassed : RunEvents.TaskFailed, task.Id, reason);
        return new TaskOutcome(task.Id, result, reason);
    }
}
