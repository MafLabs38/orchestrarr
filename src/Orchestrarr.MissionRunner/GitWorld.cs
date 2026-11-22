using Orchestrarr.Core.Missions;

namespace Orchestrarr.MissionRunner;

/// <summary>
/// The work of the Git world for a pull request: it asks the act gateway to merge it, and waits while only its pipeline is standing in the way; once it is merged, it waits for the pipeline of the integration branch on the commit that the merge
/// made, and hands over what happened. **The runner never merges and never reads the forge**: the control plane checks the facts, merges with its own credential and says what the pipeline shows (ADR 0043).
/// </summary>
public sealed class GitWorld(IMissionControlPlane controlPlane, MissionRunnerOptions options) : IWorldWork
{
    public string World => "Git";

    public async Task<MissionHandOver> RunAsync(ClaimedMission mission, Func<string, Task> report, CancellationToken cancellationToken)
    {
        // A change that was repaired in staging is carried by a pull request that the gateway opens first; one that was not has its own
        int? repairNumber = null;
        var opened = await controlPlane.OpenPullRequestAsync(mission.MissionId, cancellationToken);
        if (opened.Refusal is { } notOpened)
        {
            return new MissionHandOver($"The pull request that carries the repair of {mission.Repository} was not opened: {notOpened}.", $"The act gateway did not open the pull request: {notOpened}.", ["open-pull-request:refused"], [notOpened], Complex: false, ComplexReason: null, Commit: null);
        }
        if (opened.Opened)
        {
            await report($"Opened pull request #{opened.Number} for the repair of #{mission.PullRequest}");
            repairNumber = opened.Number;
        }
        var deadline = DateTimeOffset.UtcNow.AddMinutes(options.PipelineWaitMinutes);
        MissionActResponse merge;
        while (true)
        {
            merge = await controlPlane.MergeAsync(mission.MissionId, cancellationToken);
            if (merge.Allowed || !merge.Retry || DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }
            await report("Waiting for the pipeline of the pull request before the merge");
            await Task.Delay(TimeSpan.FromSeconds(options.PipelineAskSeconds), cancellationToken);
        }
        if (!merge.Allowed || merge.Commit is not { Length: > 0 } commit)
        {
            var why = merge.Failures.Count > 0 ? string.Join(", ", merge.Failures) : merge.Refusal ?? "refused";
            return new MissionHandOver($"The pull request of {mission.Repository} was not merged: {why}.", $"The act gateway did not allow the merge: {why}.", ["merge:refused"], [why], Complex: false, ComplexReason: null, Commit: null);
        }

        await report($"Merged. Waiting for the pipeline of the integration branch on {Short(commit)}");
        // The pipeline of the integration branch has its own time: the wait before the merge does not eat it
        deadline = DateTimeOffset.UtcNow.AddMinutes(options.PipelineWaitMinutes);
        string state;
        while (true)
        {
            state = (await controlPlane.PipelineAsync(mission.MissionId, cancellationToken)).State;
            if (state is MissionPipelineState.Passing or MissionPipelineState.Failing || DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(options.PipelineAskSeconds), cancellationToken);
        }
        await report($"The pipeline of the integration branch is {state}");
        var claim = state == MissionPipelineState.Passing
            ? $"{Subject(mission, repairNumber)} is merged into the integration branch as {Short(commit)}, and the pipeline of that commit is green."
            : $"{Subject(mission, repairNumber)} is merged into the integration branch as {Short(commit)}, but the pipeline of that commit is {state}.";
        return new MissionHandOver(claim, $"The pull request is merged as {commit} (by the act gateway of the control plane, or already merged on the forge); pipeline of the integration branch: {state}.", [$"{MissionEvidence.MergeCommit}{commit}", $"{MissionEvidence.Pipeline}{state}"],
            state == MissionPipelineState.Passing ? [] : [$"The pipeline of the integration branch on {commit} is {state}"], Complex: false, ComplexReason: null, Commit: commit);
    }

    // The pull request that was merged: the one that the problem started from, or the one that carries its repair and replaces it
    private static string Subject(ClaimedMission mission, int? repairNumber) =>
        repairNumber is { } number ? $"Pull request #{number} of {mission.Repository}, which carries the repair of #{mission.PullRequest} and replaces it," : $"Pull request of {mission.Repository}";

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;
}
