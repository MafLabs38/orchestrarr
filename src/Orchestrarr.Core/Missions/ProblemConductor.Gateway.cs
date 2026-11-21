namespace Orchestrarr.Core.Missions;

/// <summary>The act gateway's side in the control plane: the authorisation of a merge or a deployment at the moment it is done (ARCHITECTURE section 6).</summary>
public sealed partial class ProblemConductor
{
    /// <summary>
    /// The **act gateway** (ARCHITECTURE section 5): before a merge or a deployment is performed, the checks of the code are made again **now, on the exact commit that is about to be merged or deployed**, with the
    /// power read at this moment. A mandate says which acts may be asked for; this says whether the act may be done now. No act is performed here: the control plane's gateway does the act when this allows it. **One thing is done here, on purpose**: a change to files that the pipeline executes, where the merge may deploy, ends the mission and escalates the problem to the maintainer, so that it cannot be asked again.
    /// </summary>
    public async Task<ActAuthorisation> AuthoriseActAsync(string missionId, GatedAct act, string? commit, CancellationToken cancellationToken, string? repository = null, string? baseCommit = null)
    {
        if (missions.Get(missionId) is not { } first)
        {
            return ActAuthorisation.Refused("unknown mission");
        }
        return await LockedAsync(first.ProblemId, async () =>
        {
            var request = new ActRequest(act, commit, repository, baseCommit);
            var (mission, run, refusal) = InForce(missionId, act);
            if (mission is null || run is null)
            {
                return ActAuthorisation.Refused(refusal ?? "the mission has no mandate in force");
            }
            if (NamedWrongly(mission, request) is { } named)
            {
                return ActAuthorisation.Refused(named);
            }
            // A deployment is on the commit that the dossier of the world before named and that the controller accepted at the crossing: a commit pushed after it was read by nobody (a new crossing is needed). A dossier with no commit counts as failing
            // A merge in the Git world is not bound this way: the pull request's commits are made in that world, and what ties a release to what was proved is the staging proof (FR-WLD-14), checked at the release
            // The dossier of the world before is read once for the whole authorisation: the lock of the problem is held while it is read
            var accepted = LatestDossier(run, mission);
            if (act == GatedAct.Deploy && !string.Equals(accepted?.Commit, commit, StringComparison.Ordinal))
            {
                await BestEffortAsync(MissionAuditEvents.ActRefused, mission.Id, Text($"{act} on {commit}: not the commit that the controller accepted"));
                return ActAuthorisation.Refused("the commit is not the one that the controller accepted at the crossing");
            }
            var failures = request.Changes
                ? await CheckFactsAsync(run, new FactsCheck(mission.World, mission.Frame, mission.Id, accepted, HasPreviousDossier: PreviousDossierExists(run, mission, accepted), Commit: commit, Issuing: false, new FactsScope(Repository: repository, BaseCommit: baseCommit)), cancellationToken)
                : [];
            if (failures.Count > 0)
            {
                await BestEffortAsync(MissionAuditEvents.ActRefused, mission.Id, Text($"{act} on {commit ?? "an unknown commit"}: {string.Join(", ", failures)}"));
                if (failures.Contains(PreconditionFailure.ExecutedFilesTouchedWhereMergeDeploys))
                {
                    // The act is refused at the gateway, not decided by the controller: the mission ends as stopped (the same reason, the same person decides)
                    _ = await EscalateAsync(run, mission, ExecutedFilesReason, MissionEvent.Stopped);
                }
                return ActAuthorisation.Refused("a check of the code failed", failures);
            }
            return await RequiredAsync(MissionAuditEvents.ActAuthorised, mission.Id, Text($"{act} on {commit ?? "no commit"}"), cancellationToken)
                ? new ActAuthorisation(true, null, [], commit, repository, baseCommit)
                : ActAuthorisation.Refused("the audit trail cannot be written: no power is granted");
        }, cancellationToken);
    }

    /// <summary>The commit that the controller accepted at the crossing into the world of this mission (the one the world before handed over): what a release deploys. Null when there is none.</summary>
    public string? AcceptedCommit(string missionId) =>
        missions.Get(missionId) is { } mission && problems.Get(mission.ProblemId) is { } run ? LatestDossier(run, mission)?.Commit : null;

    // What an act asks for: the act, and where and on what it is to be done
    private readonly record struct ActRequest(GatedAct Act, string? Commit, string? Repository, string? BaseCommit)
    {
        // The acts that change what runs (a merge, a deployment) need the facts of this moment **on a named commit**: the gateway performs the act on that commit and no other
        public bool Changes => Act is GatedAct.MergePullRequest or GatedAct.Deploy;

        // Every act on a repository is on a named repository of the frame, opening a pull request included
        public bool OnARepository => Act is GatedAct.OpenPullRequest || Changes;
    }

    // The mission has a mandate in force, the mandate lists the act and has not expired, no stop is engaged, the powers of the problem still reach this world, and what was stored verifies again (it is not trusted:
    // the mandate is the control plane's own signature and falls inside the certificate of the authority). Returns the mission and its problem, or why not.
    private (Mission? Mission, ProblemRun? Run, string? Refusal) InForce(string missionId, GatedAct act)
    {
        if (missions.Get(missionId) is not { State: MissionState.Mandated or MissionState.Running } mission || mission.MandateId is not { } mandateId
            || mandates.Get(mandateId) is not { } signed || mandates.IsRevoked(mandateId))
        {
            return (null, null, "the mission has no mandate in force");
        }
        if (time.GetUtcNow() >= signed.Mandate.ExpiresAt || !signed.Mandate.Acts.Contains(act))
        {
            return (null, null, "the mandate has expired or does not list this act");
        }
        if (stop.Engaged)
        {
            return (null, null, "the global stop is engaged");
        }
        if (problems.Get(mission.ProblemId) is not { State: ProblemState.InProgress } run || HighestNow(run.Entry, run.Approval) is not { } highest || mission.World > highest)
        {
            return (null, null, "the powers of the problem no longer reach this world, or the certificate was withdrawn");
        }
        return !signer.Verify(signed) || approvalVerifier.Check(run.Approval, signed.Mandate) != ApprovalRefusal.None
            ? (null, null, "the mandate or its certificate does not verify")
            : (mission, run, null);
    }

    // The gateway acts where it is told, so what it is told is checked: a repository that is named and in the frame, and for a merge or a deployment a named commit and base
    private static string? NamedWrongly(Mission mission, ActRequest request)
    {
        if (request.Changes && (string.IsNullOrWhiteSpace(request.Commit) || string.IsNullOrWhiteSpace(request.Repository) || string.IsNullOrWhiteSpace(request.BaseCommit)))
        {
            return "a merge or a deployment is authorised on a named repository, commit and base";
        }
        if (!request.OnARepository)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(request.Repository))
        {
            return "an act on a repository is authorised on a named repository";
        }
        return mission.Frame.Repositories.Contains(request.Repository, StringComparer.Ordinal) ? null : "the repository is not in the frame of the mission";
    }

    // Whether the world before this one left a dossier, which the mission of a world needs (a problem with no staging has none to show)
    private static bool PreviousDossierExists(ProblemRun run, Mission mission, Dossier? accepted) =>
        mission.World == World.Staging || (mission.World == World.Git && !run.UsesStaging) || accepted is not null;

    // The dossier of the world before, **of this run**: the missions of an earlier run of the same problem (under an earlier approval) are not looked at, and the newest of a world wins
    private Dossier? LatestDossier(ProblemRun run, Mission mission) =>
        missions.ForProblem(run.ProblemId)
            .Select((other, order) => (Mission: other, Order: order))
            .Where(entry => entry.Mission.World < mission.World && entry.Mission.MandateId is { } id && mandates.Get(id) is { } signed && string.Equals(signed.Mandate.ApprovalId, run.Approval.Certificate.Id, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.Mission.World).ThenByDescending(entry => entry.Order)
            .Select(entry => dossiers.Get(entry.Mission.Id)).FirstOrDefault(found => found is not null);
}
