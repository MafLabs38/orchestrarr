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
            if (missions.Get(missionId) is not { State: MissionState.Mandated or MissionState.Running } mission || mission.MandateId is not { } mandateId
                || mandates.Get(mandateId) is not { } signed || mandates.IsRevoked(mandateId))
            {
                return ActAuthorisation.Refused("the mission has no mandate in force");
            }
            if (time.GetUtcNow() >= signed.Mandate.ExpiresAt || !signed.Mandate.Acts.Contains(act))
            {
                return ActAuthorisation.Refused("the mandate has expired or does not list this act");
            }
            if (stop.Engaged)
            {
                return ActAuthorisation.Refused("the global stop is engaged");
            }
            if (problems.Get(mission.ProblemId) is not { State: ProblemState.InProgress } run || HighestNow(run.Entry, run.Approval) is not { } highest || mission.World > highest)
            {
                return ActAuthorisation.Refused("the powers of the problem no longer reach this world, or the certificate was withdrawn");
            }
            // What was stored is verified again, not trusted: the mandate is the control plane's own signature and falls inside the certificate of the authority
            if (!signer.Verify(signed) || approvalVerifier.Check(run.Approval, signed.Mandate) != ApprovalRefusal.None)
            {
                return ActAuthorisation.Refused("the mandate or its certificate does not verify");
            }
            // The acts that change what runs (a merge, a deployment) need the facts of this moment **on a named commit**: the gateway performs the act on that commit and no other
            var changes = act is GatedAct.MergePullRequest or GatedAct.Deploy;
            if (changes && (string.IsNullOrWhiteSpace(commit) || string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(baseCommit)))
            {
                return ActAuthorisation.Refused("a merge or a deployment is authorised on a named repository, commit and base");
            }
            // Every act on a repository is on a named repository of the frame, opening a pull request included: the gateway acts where it is told, so what it is told is checked
            if (act is GatedAct.OpenPullRequest or GatedAct.MergePullRequest or GatedAct.Deploy)
            {
                if (string.IsNullOrWhiteSpace(repository))
                {
                    return ActAuthorisation.Refused("an act on a repository is authorised on a named repository");
                }
                if (!mission.Frame.Repositories.Contains(repository, StringComparer.Ordinal))
                {
                    return ActAuthorisation.Refused("the repository is not in the frame of the mission");
                }
            }
            // A deployment is on the commit that the dossier of the world before named and that the controller accepted at the crossing: a commit pushed after it was read by nobody (a new crossing is needed). A dossier with no commit counts as failing.
            // A merge in the Git world is not bound this way: the pull request's commits are made in that world, and what ties a release to what was proved is the staging proof (FR-WLD-14), checked at the release
            // The dossier of the world before is read once for the whole authorisation: the lock of the problem is held while it is read
            var accepted = LatestDossier(run, mission);
            if (act == GatedAct.Deploy && !string.Equals(accepted?.Commit, commit, StringComparison.Ordinal))
            {
                await BestEffortAsync(MissionAuditEvents.ActRefused, mission.Id, Text($"{act} on {commit}: not the commit that the controller accepted"));
                return ActAuthorisation.Refused("the commit is not the one that the controller accepted at the crossing");
            }
            var failures = changes
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
