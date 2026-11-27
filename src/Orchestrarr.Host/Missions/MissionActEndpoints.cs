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
            ProblemConductor conductor, MissionProgress progress, RepairRegistry? repairs, IDossierStore dossiers, ILiveNotifier live, CancellationToken ct) =>
        {
            if (!Gate(id, runnerName(context), missions, mandates, problems, out var entry) || Effective(repairs, missions.Get(id)!.ProblemId, entry) is not var (number, head))
            {
                return Results.NotFound();
            }
            // A repair that was made and is not in a pull request (it has not been opened, or what held it was lost) is not merged around: the pull request that the problem started from is the one that did not pass
            if (RepairIsOutstanding(repairs, missions, dossiers, missions.Get(id)!.ProblemId))
            {
                return Results.Ok(new MissionActResponse(false, "the repair of this problem is not in a pull request: it is opened first, and a repair that was lost goes back to staging", [], Retry: false, null));
            }
            var repository = entry.Repositories[0];
            if (await pulls.ReadAsync(repository, number, ct) is not { } read)
            {
                return Results.Ok(new MissionActResponse(false, "the pull request cannot be read now", [], Retry: true, null));
            }
            // A pull request that is found merged is reported, not merged twice, and what the control plane found is written down: it may have been this gateway's merge, whose answer did not reach the runner, or someone else's
            if (read.Merged)
            {
                if (!string.Equals(read.HeadSha, head, StringComparison.Ordinal))
                {
                    return Results.Ok(new MissionActResponse(false, "the pull request was merged at another head than the one that was approved", [], Retry: false, null));
                }
                _ = await conductor.RecordActPerformedAsync(id, GatedAct.MergePullRequest, $"{repository}#{number} found already merged as {read.MergeCommit}, at the approved head (by this gateway earlier, or not)", ct);
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
            // The act is written down as the authorisation was: the trail says that the control plane merged, on what, and what came of it
            if (!await conductor.RecordActPerformedAsync(id, GatedAct.MergePullRequest, merged.Merged ? $"{repository}#{number} merged as {merged.Commit}" : $"{repository}#{number} refused by the forge: {merged.Error}", ct))
            {
                progress.Add(id, "the audit trail could not be written after the merge");
            }
            // A pull request that carried a repair replaced the one the problem started from: that one is closed, saying where it went
            if (merged.Merged && repairs?.FixOf(missions.Get(id)!.ProblemId) is { } fix && entry.PullRequest is { } original)
            {
                var closed = await forge.ClosePullRequestAsync(repository, original, $"Replaced by #{fix.Number}, which carries the same change with the repair that it needed, and is merged.", ct);
                progress.Add(id, closed ? $"closed pull request #{original}, replaced by #{fix.Number}" : $"pull request #{original} could not be closed");
                if (!await conductor.RecordActPerformedAsync(id, GatedAct.MergePullRequest, closed ? $"{repository}#{original} closed, replaced by #{fix.Number}" : $"{repository}#{original} could not be closed (replaced by #{fix.Number})", ct))
                {
                    progress.Add(id, "the audit trail could not be written after the close");
                }
            }
            live.Publish(LiveTopics.Missions, id);
            // A forge that refuses because a check it requires has not reported yet (the pipeline of the pull request was only just queued) is a wait, not a refusal: the runner asks again
            return Results.Ok(new MissionActResponse(merged.Merged, merged.Merged ? null : merged.Error, [], Retry: !merged.Merged && merged.ChecksPending, merged.Commit));
        });

        // The pull request that carries the repair of a staging runner: the gateway makes the branch and the commit itself from the files that it checked, so that the runner never holds a credential that writes to a repository
        group.MapPost("/missions/{id}/acts/open-pull-request", async (string id, HttpContext context, IMissionStore missions, IMandateLedger mandates, IProblemStore problems, IPullRequestFacts pulls, IDefaultBranchSource defaults, IBranchSource branches, IForgeActs forge,
            ProblemConductor conductor, MissionProgress progress, RepairRegistry? repairs, IDossierStore dossiers, ILiveNotifier live, CancellationToken ct) =>
        {
            if (!Gate(id, runnerName(context), missions, mandates, problems, out var entry) || repairs is null)
            {
                return Results.NotFound();
            }
            var problemId = missions.Get(id)!.ProblemId;
            var repository = entry.Repositories[0];
            if (repairs.FixOf(problemId) is { } already)
            {
                return Results.Ok(new MissionPullRequestResponse(true, already.Number, already.Head, null, already.OriginalHead));
            }
            if (repairs.PendingOf(problemId) is not { } repair)
            {
                // A repair that the staging dossier says was made and that is not kept any more (a restart) is lost, and is not replaced by the pull request that did not pass; else nothing was repaired
                return Results.Ok(new MissionPullRequestResponse(false, 0, null, RepairWasMade(missions, dossiers, problemId) ? "the repair was made but is no longer held (the control plane restarted): the problem goes back to staging" : null));
            }
            string baseBranch;
            if (entry.PullRequest is { } original)
            {
                if (await pulls.ReadAsync(repository, original, ct) is not { TargetsDefaultBranch: true, DefaultBranch: { } readBranch } read || !string.Equals(read.HeadSha, repair.OriginalHead, StringComparison.Ordinal))
                {
                    return Results.Ok(new MissionPullRequestResponse(false, 0, null, "the pull request cannot be read, does not target the default branch, or moved since the repair was made"));
                }
                baseBranch = readBranch;
            }
            else
            {
                // A change that has no pull request is made against the head of the default branch: if that moved since the runner worked, the change was not proved on the code that it would land on
                if (await defaults.ReadAsync(repository, ct) is not { } current || !string.Equals(current.HeadSha, repair.OriginalHead, StringComparison.Ordinal))
                {
                    return Results.Ok(new MissionPullRequestResponse(false, 0, null, "the default branch cannot be read, or moved since the change was made: the problem goes back to staging"));
                }
                baseBranch = current.Branch;
            }
            // The frame of the mission applies to what is written, not only to what was read: the paths that the maintainer's certificate forbids too
            if (repair.Branch is { } finished)
            {
                // The branch of an agent: it was audited at one commit, and the pull request is opened from that commit only (the agent may have pushed again after it said it had finished)
                if (await branches.HeadOfAsync(repository, finished.Name, ct) is not { } atNow || !string.Equals(atNow, finished.Sha, StringComparison.Ordinal))
                {
                    return Results.Ok(new MissionPullRequestResponse(false, 0, null, "the branch is no longer at the commit that was audited: the problem goes back to staging"));
                }
            }
            else if (RepairPolicy.Refusal(repair.Files, missions.Get(id)!.Frame.ForbiddenPaths, await RepairBase.ReadAsync(branches, repository, repair.OriginalHead, repair.Files, ct)) is { } forbidden)
            {
                return Results.Ok(new MissionPullRequestResponse(false, 0, null, forbidden));
            }
            var authorisation = await conductor.AuthoriseActAsync(id, GatedAct.OpenPullRequest, null, ct, repository);
            if (!authorisation.Allowed)
            {
                progress.Add(id, $"the pull request is not allowed now: {Describe(authorisation)}");
                return Results.Ok(new MissionPullRequestResponse(false, 0, null, authorisation.Refusal));
            }
            var (title, body) = entry.PullRequest is { } replaced
                ? ($"fix: repair #{replaced} so that it passes", $"Opened by Orchestrarr for pull request #{replaced}, which did not pass in staging. The change of #{replaced} is carried here with a repair of the source of the application ({repair.Files.Count} file(s)), proved in staging.")
                : ($"fix: {Titled(entry.ProblemClass)} of {repository}", $"Opened by Orchestrarr for a problem that has no pull request ({entry.ProblemClass}). A worker agent made the change ({repair.Branch?.Paths.Count ?? 0} file(s)) in an isolated staging machine and pushed it to a branch; the control plane read that commit on the forge and audited it before this pull request was opened.");
            OpenedPullRequest opened;
            if (repair.Branch is { } agentBranch)
            {
                opened = await forge.OpenPullRequestFromBranchAsync(repository, baseBranch, agentBranch.Name, agentBranch.Sha, title, body, ct);
            }
            else
            {
                // A branch name that is new at each attempt: the same problem can be repaired again after a loop, and a name that exists is refused by the forge
                var branch = $"orchestrarr/{problemId}-{Guid.NewGuid().ToString("N")[..6]}";
                opened = await forge.OpenPullRequestAsync(repository, baseBranch, repair.OriginalHead, branch, repair.Files, title, body, ct);
            }
            progress.Add(id, opened.Opened ? $"opened pull request #{opened.Number} for the repair ({Short(opened.HeadSha)})" : $"the forge refused the pull request: {opened.Error}");
            if (opened.Opened && opened.HeadSha is { } head)
            {
                repairs.Opened(problemId, opened.Number, head, repair.OriginalHead);
            }
            var forWhat = entry.PullRequest is { } replacedNumber ? $"the repair of #{replacedNumber}" : "the change that solves the problem";
            var performed = opened.Opened ? $"{repository}#{opened.Number} opened for {forWhat}, head {opened.HeadSha}" : $"{repository}: refused by the forge for {forWhat}: {opened.Error}";
            if (!await conductor.RecordActPerformedAsync(id, GatedAct.OpenPullRequest, performed, ct))
            {
                progress.Add(id, "the audit trail could not be written after the pull request");
            }
            live.Publish(LiveTopics.Missions, id);
            return Results.Ok(new MissionPullRequestResponse(opened.Opened, opened.Number, opened.HeadSha, opened.Opened ? null : opened.Error, repair.OriginalHead));
        });

        group.MapPost("/missions/{id}/acts/deploy", async (string id, HttpContext context, IMissionStore missions, IMandateLedger mandates, IProblemStore problems, IPullRequestFacts pulls, ProblemConductor conductor,
            MissionProgress progress, RepairRegistry? repairs, IDossierStore dossiers, ILiveNotifier live, CancellationToken ct) =>
        {
            if (!Gate(id, runnerName(context), missions, mandates, problems, out var entry) || Effective(repairs, missions.Get(id)!.ProblemId, entry) is not var (number, _))
            {
                return Results.NotFound();
            }
            if (RepairIsOutstanding(repairs, missions, dossiers, missions.Get(id)!.ProblemId))
            {
                return Results.Ok(new MissionActResponse(false, "the repair of this problem is not in a pull request", [], Retry: false, null));
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

        group.MapGet("/missions/{id}/pipeline", async (string id, HttpContext context, IMissionStore missions, IMandateLedger mandates, IProblemStore problems, IPullRequestFacts pulls, RepairRegistry? repairs, CancellationToken ct) =>
        {
            if (!MissionRunnerEndpoints.Owns(id, runnerName(context), missions, mandates) || problems.Get(missions.Get(id)?.ProblemId ?? string.Empty)?.Entry is not { Repositories.Count: 1 } entry
                || Effective(repairs, missions.Get(id)!.ProblemId, entry) is not var (number, _))
            {
                return Results.NotFound();
            }
            if (await pulls.ReadAsync(entry.Repositories[0], number, ct) is not { } read)
            {
                return Results.Ok(new MissionPipelineResponse(MissionPipelineState.Unknown, null));
            }
            return Results.Ok(read.Merged ? new MissionPipelineResponse(MissionPipelineState.Of(read.MergeChecks), read.MergeCommit) : new MissionPipelineResponse(MissionPipelineState.Of(read.Checks), read.HeadSha));
        });
    }

    // The staging dossier of the problem says that a repair was made (the evidence says it, whatever the control plane still holds)
    private static bool RepairWasMade(IMissionStore missions, IDossierStore dossiers, string problemId) =>
        missions.ForProblem(problemId).Where(mission => mission.World == World.Staging).Select(mission => dossiers.Get(mission.Id)).LastOrDefault(dossier => dossier is not null)
            ?.EvidenceDigests.Any(digest => digest.StartsWith(MissionEvidence.Repair, StringComparison.Ordinal)) == true;

    // A repair was made and no pull request carries it
    private static bool RepairIsOutstanding(RepairRegistry? repairs, IMissionStore missions, IDossierStore dossiers, string problemId) =>
        repairs?.FixOf(problemId) is null && RepairWasMade(missions, dossiers, problemId);

    private static (int Number, string Head)? Effective(RepairRegistry? repairs, string problemId, PlanEntry entry) =>
        repairs?.Effective(problemId, entry) ?? (entry is { PullRequest: { } number, HeadSha: { } head } ? (number, head) : null);

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
    // A pull request that was just opened has the checks of its push and none yet of the pull request: the forge reads green while the result that counts is not known. Only a red pipeline is a reason not to wait (ADR 0055)
    private static bool WaitingForPipeline(ActAuthorisation authorisation, ChecksState checks) =>
        checks is not ChecksState.Failing && authorisation.Failures.Count > 0
        && authorisation.Failures.All(failure => failure is PreconditionFailure.CiNotGreenOnExactCommit or PreconditionFailure.CiResultNotKnown);

    private static string Describe(ActAuthorisation authorisation) =>
        authorisation.Failures.Count > 0 ? string.Join(", ", authorisation.Failures) : authorisation.Refusal ?? "refused";

    private static string Short(string? sha) => sha is { Length: > 8 } ? sha[..8] : sha ?? "unknown";

    // The class of a problem as a few words for a title ("DependencyAlert" is "dependency alert"): plain letters only, whatever the problem carries
    private static string Titled(string problemClass)
    {
        var words = new System.Text.StringBuilder();
        var previousIsLetter = false;
        foreach (var letter in problemClass)
        {
            if (!char.IsLetter(letter))
            {
                previousIsLetter = false;
                continue;
            }
            // A word starts after a character that is not a letter, or on a capital that follows a letter
            if (words.Length > 0 && (!previousIsLetter || char.IsUpper(letter)))
            {
                words.Append(' ');
            }
            words.Append(char.ToLowerInvariant(letter));
            previousIsLetter = true;
        }
        return words.Length == 0 ? "a problem" : words.ToString();
    }
}
