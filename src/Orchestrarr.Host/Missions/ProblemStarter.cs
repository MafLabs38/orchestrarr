using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Missions;

/// <summary>A row that is ready to start (its plan entry with the problem as read and the lead's plan), or why it will not start: one of the two, never both.</summary>
internal sealed record PreparedProblem(string RowId, PlanEntry? Entry, ValidatedProblem? Refused);

/// <summary>
/// Starts the problems of the test bed, **one work site at a time on a repository** (ADR 0049): the decision "start now, or wait in line" and the start itself are made under one gate per repository, by the endpoint that validates and by the
/// driver that takes the line, so that two requests, or a request and a pass of the driver, never both find the repository free. A problem that waits is planned and not started: it has no mandate and uses no approval until it starts.
/// </summary>
internal sealed class ProblemStarter(IServiceProvider services, MissionsOptions options, ProblemParking parking, IProblemStore problems, ProblemQueue line, ProblemLocks locks, ProblemConductor conductor, MissionProgress progress, IMissionAudit audit, IStopSignal stop, TimeProvider time)
{
    /// <summary>
    /// How long a problem may wait: the lifetime of the approval that was proved for it (two hours). A problem that waited longer is not started on a validation that old: the maintainer validates again.
    /// </summary>
    internal static readonly TimeSpan QueuedLifetime = TimeSpan.FromHours(2);

    /// <summary>Whether a problem is in progress on the repository.</summary>
    internal bool Busy(string repository) =>
        problems.InProgress().Any(run => run.Entry.Repositories.Count == 1 && string.Equals(run.Entry.Repositories[0], repository, StringComparison.Ordinal));

    /// <summary>The problem starts when its repository is free and nothing waits before it; otherwise it goes behind the others (it is not started, and the answer says so).</summary>
    public async Task<ValidatedProblem> StartOrQueueAsync(PreparedProblem one, CancellationToken cancellationToken)
    {
        // A problem on several repositories (or none) is the conductor's to refuse: it does not wait for any of them
        if (one.Entry is not { Repositories.Count: 1 } entry)
        {
            return await StartAsync(one, cancellationToken);
        }
        var repository = entry.Repositories[0];
        var gate = locks.Of(GateKey(repository));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (line.Contains(entry.ProblemId))
            {
                return new ValidatedProblem(one.RowId, entry.ProblemId, null, null, "alreadyQueued");
            }
            // A pull request that its bot made before an earlier operation of the lot was merged is behind its base: the bot is asked to rebase it and the lot goes on with the next operation (ADR 0055)
            if (problems.Get(entry.ProblemId) is not { State: ProblemState.InProgress })
            {
                var verdict = await JudgeAsync(entry, cancellationToken);
                switch (verdict.Kind)
                {
                    case Freshness.Left:
                        _ = await audit.RecordAsync(MissionAuditEvents.QueuedProblemRefused, entry.ProblemId, $"not started: {verdict.Reason}", CancellationToken.None);
                        return new ValidatedProblem(one.RowId, entry.ProblemId, null, null, "thePullRequestMoved");
                    case Freshness.Followed:
                        entry = entry with { HeadSha = verdict.Head };
                        one = one with { Entry = entry };
                        _ = await audit.RecordAsync(MissionAuditEvents.ProblemFollowedBot, entry.ProblemId, $"the bot made the pull request again: the work follows its new head {Short(verdict.Head)}", CancellationToken.None);
                        break;
                    case Freshness.NeedsRebase:
                        _ = line.Enqueue(new QueuedProblem(one.RowId, entry, time.GetUtcNow()));
                        await ParkAsync(entry, verdict.Reason!, null, cancellationToken);
                        return new ValidatedProblem(one.RowId, entry.ProblemId, null, null, null, Queued: true);
                    default:
                        break;
                }
            }
            // A problem already in progress is the conductor's to refuse ("already being worked on"), not a reason to queue it again
            if (problems.Get(entry.ProblemId) is { State: ProblemState.InProgress } || (!line.Waits(repository, parked => parking.IsParked(parked.Entry.ProblemId)) && !Busy(repository)))
            {
                return await StartAsync(one, cancellationToken);
            }
            _ = line.Enqueue(new QueuedProblem(one.RowId, entry, time.GetUtcNow()));
            // The trail is written whatever the client does with its request
            _ = await audit.RecordAsync(MissionAuditEvents.ProblemQueued, entry.ProblemId, $"waits for its turn on {repository}: another problem is being done there", CancellationToken.None);
            return new ValidatedProblem(one.RowId, entry.ProblemId, null, null, null, Queued: true);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    /// <summary>
    /// A pass over the line: with the global stop engaged the line is emptied (nothing starts on a validation that a stop interrupted); a problem that waited longer than its approval lasts leaves; then the first problem of each repository
    /// that is free starts. Whatever happens to one problem, the others are still looked at.
    /// </summary>
    public async Task StartReadyAsync(CancellationToken cancellationToken)
    {
        if (line.Waiting().Count == 0)
        {
            return;
        }
        if (stop.Engaged)
        {
            foreach (var dropped in line.Clear())
            {
                _ = await audit.RecordAsync(MissionAuditEvents.QueuedProblemRefused, dropped.Entry.ProblemId, "left the line without starting: the global stop is engaged", CancellationToken.None);
            }
            return;
        }
        foreach (var old in line.Waiting().Where(waiting => time.GetUtcNow() - waiting.QueuedAt >= QueuedLifetime).Where(waiting => line.Remove(waiting.Entry.ProblemId)))
        {
            _ = await audit.RecordAsync(MissionAuditEvents.QueuedProblemRefused, old.Entry.ProblemId, "left the line without starting: it waited longer than its approval lasts, the maintainer validates again", CancellationToken.None);
        }
        foreach (var repository in line.Waiting().Select(waiting => waiting.Entry.Repositories.Count == 1 ? waiting.Entry.Repositories[0] : null).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var gate = locks.Of(GateKey(repository));
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (!Busy(repository))
                {
                    await FreshenAsync(repository, cancellationToken);
                }
                foreach (var ready in line.TakeReady(other => !string.Equals(other, repository, StringComparison.Ordinal) || Busy(other), waiting => !parking.IsParked(waiting.Entry.ProblemId)))
                {
                    parking.Release(ready.Entry.ProblemId);
                    await StartQueuedAsync(ready, cancellationToken);
                }
            }
            finally
            {
                _ = gate.Release();
            }
        }
    }

    // The pull requests of a free lot that wait for their turn, in the order of the route: each is read on the forge, and what a bot did (or must do) is applied. The first one that is ready stops the pass (it is the one that starts); a
    // parked one is read again only every half minute, and the lot goes on with the next operation of its route meanwhile
    private async Task FreshenAsync(string repository, CancellationToken cancellationToken)
    {
        var candidates = line.Waiting().Where(waiting => waiting.Entry.Repositories.Count == 1 && waiting.Entry.Repositories[0] == repository && waiting.Entry.PullRequest is not null)
            .OrderBy(waiting => RouteRank.Of(waiting.Entry)).ThenBy(waiting => waiting.QueuedAt).Select(waiting => waiting.Entry).ToList();
        foreach (var entry in candidates)
        {
            var id = entry.ProblemId;
            if (parking.Get(id) is { } parked && time.GetUtcNow() - parked.LastChecked < TimeSpan.FromSeconds(Math.Max(0, options.BotRebaseRecheckSeconds)))
            {
                continue;
            }
            var verdict = await JudgeAsync(entry, cancellationToken);
            switch (verdict.Kind)
            {
                case Freshness.Left:
                    _ = line.Remove(id);
                    _ = parking.Release(id);
                    _ = await audit.RecordAsync(MissionAuditEvents.QueuedProblemRefused, id, $"left the line without starting: {verdict.Reason}", CancellationToken.None);
                    break;
                case Freshness.NeedsRebase:
                    await ParkAsync(entry, verdict.Reason!, parking.Get(id), cancellationToken);
                    break;
                case Freshness.Followed:
                    _ = line.Replace(id, entry with { HeadSha = verdict.Head });
                    _ = parking.Release(id);
                    _ = await audit.RecordAsync(MissionAuditEvents.ProblemFollowedBot, id, $"the bot made the pull request again: the work follows its new head {Short(verdict.Head)}", CancellationToken.None);
                    return;
                default:
                    _ = parking.Release(id);
                    return;
            }
        }
    }

    // The pull request is behind its base or in conflict: the bot is asked to rebase it (at most BotRebaseRequests times), and after BotRebaseWaitMinutes without a new head the problem is put aside with the reason
    private async Task ParkAsync(PlanEntry entry, string reason, ParkedProblem? before, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var since = before?.Since ?? now;
        var id = entry.ProblemId;
        if (now - since >= TimeSpan.FromMinutes(Math.Max(1, options.BotRebaseWaitMinutes)))
        {
            _ = line.Remove(id);
            _ = parking.Release(id);
            _ = await audit.RecordAsync(MissionAuditEvents.QueuedProblemRefused, id, $"put aside: the bot did not rebase the pull request within {options.BotRebaseWaitMinutes} minutes ({reason})", CancellationToken.None);
            return;
        }
        var requests = before?.Requests ?? 0;
        var lastRequested = before?.LastRequested;
        var due = lastRequested is null || now - lastRequested >= TimeSpan.FromMinutes(Math.Max(1, options.BotRebaseWaitMinutes) / (double)Math.Max(1, options.BotRebaseRequests));
        if (requests < options.BotRebaseRequests && due && entry.PullRequest is { } number && services.GetService<IForgeActs>() is { } acts)
        {
            var asked = await acts.RequestBotRebaseAsync(entry.Repositories[0], number, "@dependabot rebase", cancellationToken);
            _ = await audit.RecordAsync(MissionAuditEvents.BotRebaseRequested, id, asked ? $"asked the bot to rebase the pull request ({reason})" : $"the forge did not take the request to rebase the pull request ({reason})", CancellationToken.None);
            requests++;
            lastRequested = now;
        }
        if (before is null)
        {
            _ = await audit.RecordAsync(MissionAuditEvents.ProblemParked, id, $"waits for its bot to rebase the pull request, the lot goes on: {reason}", CancellationToken.None);
        }
        parking.Set(id, new ParkedProblem(since, requests, now, lastRequested, reason));
    }

    /// <summary>
    /// The runner refused a pull request before building (it conflicts with its base, or it moved: its bot made it again while it was being proved). A **bot's** pull request is put back in the line on the head it is at now, a bounded number
    /// of times (<see cref="MissionsOptions.BotRebaseRequests"/>), and goes through the same turn (parked, rebased, followed); anyone else's is ended, as ADR 0049 said (ADR 0055).
    /// </summary>
    public async Task RequeueAfterRefusalAsync(string problemId, CancellationToken cancellationToken)
    {
        if (problems.Get(problemId) is not { Entry: { PullRequest: { } number, Repositories.Count: 1 } entry } || parking.Requeues(problemId) >= options.BotRebaseRequests
            || services.GetService<IPullRequestFacts>() is not { } facts)
        {
            return;
        }
        var read = await facts.ReadAsync(entry.Repositories[0], number, cancellationToken);
        if (read is null || read.Merged || read.Closed || !IsBot(read.Author))
        {
            return;
        }
        var gate = locks.Of(GateKey(entry.Repositories[0]));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (line.Contains(problemId))
            {
                return;
            }
            _ = parking.AddRequeue(problemId);
            _ = line.Enqueue(new QueuedProblem($"{DecisionTable.PullRequestRowPrefix}{entry.Repositories[0]}#{number}", entry with { HeadSha = read.HeadSha }, time.GetUtcNow()));
            _ = await audit.RecordAsync(MissionAuditEvents.ProblemQueued, problemId, "the bot made the pull request again while it was proved: it goes back in the line on its new head", CancellationToken.None);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    // What the forge says of the pull request now, against what was ticked. A problem with no pull request, or a forge that does not answer, is ready as it was
    private async Task<FreshnessVerdict> JudgeAsync(PlanEntry entry, CancellationToken cancellationToken)
    {
        if (entry.PullRequest is not { } number || entry.Repositories.Count != 1 || services.GetService<IPullRequestFacts>() is not { } facts)
        {
            return new FreshnessVerdict(Freshness.Ready, null, null);
        }
        var read = await facts.ReadAsync(entry.Repositories[0], number, cancellationToken);
        // The forge works out whether a pull request still merges when it is asked, a moment after a merge moved the base: an unknown state is asked again before it is taken for a clean one
        for (var attempt = 0; attempt < 3 && read is { Merged: false, Merge: MergeState.Unknown }; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.BotRebaseUnknownWaitSeconds)), cancellationToken);
            read = await facts.ReadAsync(entry.Repositories[0], number, cancellationToken);
        }
        return PullRequestFreshness.Of(entry, read, IsBot);
    }

    private bool IsBot(string? login) => login is not null && options.BotAuthors.Contains(login, StringComparer.OrdinalIgnoreCase);

    private static string Short(string? sha) => sha is { Length: > 8 } ? sha[..8] : sha ?? string.Empty;

    // A problem that leaves the line without starting says why in the trail; an exception of the start is a refusal like any other, so that it neither loses the problem silently nor stops the others
    private async Task StartQueuedAsync(QueuedProblem ready, CancellationToken cancellationToken)
    {
        string? refusal;
        try
        {
            refusal = (await StartAsync(new PreparedProblem(ready.RowId, ready.Entry, null), cancellationToken)).Refusal;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            refusal = $"the start failed ({failure.GetType().Name})";
        }
        if (refusal is not null)
        {
            _ = await audit.RecordAsync(MissionAuditEvents.QueuedProblemRefused, ready.Entry.ProblemId, $"left the line without starting: {refusal}", CancellationToken.None);
        }
    }

    internal async Task<ValidatedProblem> StartAsync(PreparedProblem one, CancellationToken cancellationToken)
    {
        if (one.Entry is not { } entry)
        {
            return one.Refused ?? new ValidatedProblem(one.RowId, null, null, null, "refused");
        }
        var id = one.RowId;
        if (services.GetService<SandboxApprovals>() is not { } approvals || !await approvals.PrepareAsync(entry, cancellationToken))
        {
            return new ValidatedProblem(id, entry.ProblemId, null, null, "noApproval");
        }
        // The staging of the test bed is declared, and its baseline is the snapshot of the lab VMs (a reset brings them back to it)
        var result = await conductor.StartAsync(entry, StagingDeclaration.DeclaredStaging, StagingRequirement.Dependencies, StagingState.BaselineProven, cancellationToken);
        if (result.Ok)
        {
            // The same row is the same problem: what a run of it made (a repair, the pull request that carried it) is not the business of the run that starts now
            services.GetService<RepairRegistry>()?.Forget(entry.ProblemId);
        }
        if (result.Ok && result.Mission is { } mission && entry.Plan is { } plan)
        {
            // What the lead decided and why is shown with the first mission of the problem
            progress.Add(mission.Id, $"the lead planned: {plan.Summary} ({string.Join(" | ", plan.Reasons)})");
            if (plan.Complex)
            {
                progress.Add(mission.Id, $"the lead marked the problem complex: {plan.ComplexReason ?? "no reason given"}");
            }
        }
        return result.Ok
            ? new ValidatedProblem(id, entry.ProblemId, result.Mission?.Id, result.Mission?.World.ToString(), null)
            : new ValidatedProblem(id, entry.ProblemId, null, null, result.Refusal ?? "refused");
    }

    // The gate is the repository's, in the same registry as the locks of the problems (their keys are problem identifiers, which never start with this prefix)
    private static string GateKey(string repository) => $"repository:{repository}";
}
