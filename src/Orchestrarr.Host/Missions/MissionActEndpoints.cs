using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Live;

namespace Orchestrarr.Host.Missions;

/// <summary>
/// **The act gateway** (ADR 0043, ARCHITECTURE section 5): the only place where a pull request is merged and where a deployment is allowed. A runner asks; the control plane reads the facts itself from the forge, has the conductor
/// authorise the act **on this exact commit, with the power read now**, and only then performs the merge with its own credential (a runner never holds one) or tells the runner that it may deploy what the controller accepted.
/// Each route is the runner's own: the mission must be its own, started, and its mandate must list the act.
/// </summary>
internal static class MissionActEndpoints
{
    public static void MapMissionActRoutes(this RouteGroupBuilder group, Func<HttpContext, string> runnerName)
    {
        group.MapPost("/missions/{id}/acts/merge", async (string id, HttpContext context, IMissionStore missions, IMandateLedger mandates, IProblemStore problems, IPullRequestFacts pulls, IForgeActs forge,
            ProblemConductor conductor, MissionProgress progress, ILiveNotifier live, CancellationToken ct) =>
        {
            if (!Gate(id, runnerName(context), missions, mandates, problems, out var entry) || entry.PullRequest is not { } number || entry.HeadSha is not { } head)
            {
                return Results.NotFound();
            }
            var repository = entry.Repositories[0];
            if (await pulls.ReadAsync(repository, number, ct) is not { } read)
            {
                return Results.Ok(new MissionActResponse(false, "the pull request cannot be read now", [], Retry: true, null));
            }
            // A merge that was done (by this gateway, before a failure of the runner's call) is reported, not done twice
            if (read.Merged)
            {
                return Results.Ok(new MissionActResponse(true, null, [], Retry: false, read.MergeCommit));
            }
            var authorisation = await conductor.AuthoriseActAsync(id, GatedAct.MergePullRequest, head, ct, repository, read.BaseSha);
            if (!authorisation.Allowed)
            {
                progress.Add(id, $"the merge is not allowed now: {Describe(authorisation)}");
                live.Publish(LiveTopics.Missions, id);
                return Results.Ok(new MissionActResponse(false, authorisation.Refusal, [.. authorisation.Failures.Select(failure => failure.ToString())], WaitingForPipeline(authorisation, read.Checks), null));
            }
            var merged = await forge.MergePullRequestAsync(repository, number, head, ct);
            progress.Add(id, merged.Merged ? $"merged pull request #{number} into the integration branch ({Short(merged.Commit)})" : $"the forge refused the merge: {merged.Error}");
            live.Publish(LiveTopics.Missions, id);
            return Results.Ok(new MissionActResponse(merged.Merged, merged.Merged ? null : merged.Error, [], Retry: false, merged.Commit));
        });

        group.MapPost("/missions/{id}/acts/deploy", async (string id, HttpContext context, IMissionStore missions, IMandateLedger mandates, IProblemStore problems, IPullRequestFacts pulls, ProblemConductor conductor,
            MissionProgress progress, ILiveNotifier live, CancellationToken ct) =>
        {
            if (!Gate(id, runnerName(context), missions, mandates, problems, out var entry) || entry.PullRequest is not { } number)
            {
                return Results.NotFound();
            }
            var repository = entry.Repositories[0];
            if (conductor.AcceptedCommit(id) is not { } commit || await pulls.ReadAsync(repository, number, ct) is not { } read)
            {
                return Results.Ok(new MissionActResponse(false, "no commit was accepted for this release, or the pull request cannot be read now", [], Retry: false, null));
            }
            var authorisation = await conductor.AuthoriseActAsync(id, GatedAct.Deploy, commit, ct, repository, read.BaseSha);
            progress.Add(id, authorisation.Allowed ? $"the deployment of {Short(commit)} is allowed" : $"the deployment is not allowed: {Describe(authorisation)}");
            live.Publish(LiveTopics.Missions, id);
            return Results.Ok(new MissionActResponse(authorisation.Allowed, authorisation.Refusal, [.. authorisation.Failures.Select(failure => failure.ToString())], Retry: false, authorisation.Allowed ? commit : null));
        });

        group.MapGet("/missions/{id}/pipeline", async (string id, HttpContext context, IMissionStore missions, IMandateLedger mandates, IProblemStore problems, IPullRequestFacts pulls, CancellationToken ct) =>
        {
            if (!MissionRunnerEndpoints.Owns(id, runnerName(context), missions, mandates) || problems.Get(missions.Get(id)?.ProblemId ?? string.Empty)?.Entry is not { PullRequest: { } number, Repositories.Count: 1 } entry)
            {
                return Results.NotFound();
            }
            if (await pulls.ReadAsync(entry.Repositories[0], number, ct) is not { } read)
            {
                return Results.Ok(new MissionPipelineResponse("unknown", null));
            }
            return Results.Ok(read.Merged ? new MissionPipelineResponse(read.MergeChecks.ToString().ToLowerInvariant(), read.MergeCommit) : new MissionPipelineResponse(read.Checks.ToString().ToLowerInvariant(), read.HeadSha));
        });
    }

    // The mission is the runner's, it has started, and its problem is a pull request on one repository
    private static bool Gate(string id, string runner, IMissionStore missions, IMandateLedger mandates, IProblemStore problems, out PlanEntry entry)
    {
        entry = null!;
        if (!MissionRunnerEndpoints.Owns(id, runner, missions, mandates) || missions.Get(id) is not { State: MissionState.Running } mission || problems.Get(mission.ProblemId)?.Entry is not { Repositories.Count: 1 } found)
        {
            return false;
        }
        entry = found;
        return true;
    }

    // Only a pipeline that is not over yet is worth asking again about: a red one, or any other check that failed, is final
    private static bool WaitingForPipeline(ActAuthorisation authorisation, ChecksState checks) =>
        checks is ChecksState.Pending or ChecksState.Unknown or ChecksState.NotRun && authorisation.Failures.Count > 0
        && authorisation.Failures.All(failure => failure is PreconditionFailure.CiNotGreenOnExactCommit or PreconditionFailure.CiResultNotKnown);

    private static string Describe(ActAuthorisation authorisation) =>
        authorisation.Failures.Count > 0 ? string.Join(", ", authorisation.Failures) : authorisation.Refusal ?? "refused";

    private static string Short(string? sha) => sha is { Length: > 8 } ? sha[..8] : sha ?? "unknown";
}
