using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Missions;

/// <summary>
/// The pull requests of bots in the line (ADR 0055), one place for what a bot did or must do: the forge is read when the turn comes (<see cref="PullRequestFreshness"/>), and the verdict is applied the same way whether the problem was
/// just validated or waits in the line: followed on the bot's new head, parked while its bot is asked to rebase it, done by another operation when its bot closed it, or put aside with the reason. The starter of problems decides when;
/// this decides what.
/// </summary>
internal sealed class BotPullRequests(IServiceProvider services, MissionsOptions options, ProblemParking parking, IProblemStore problems, ProblemQueue line, ProblemLocks locks, IMissionAudit audit, TimeProvider time)
{
    // How many times a merge state that the forge has not worked out yet is read again
    private const int UnknownReads = 3;

    // What the forge says of the pull request now, against what was ticked. A problem with no pull request, or a forge that does not answer, is ready as it was
    internal async Task<FreshnessVerdict> JudgeAsync(PlanEntry entry, CancellationToken cancellationToken)
    {
        if (entry.PullRequest is not { } number || entry.Repositories.Count != 1 || services.GetService<IPullRequestFacts>() is not { } facts)
        {
            return new FreshnessVerdict(Freshness.Ready, null, null);
        }
        var read = await facts.ReadAsync(entry.Repositories[0], number, cancellationToken);
        // The forge works out whether a pull request still merges when it is asked, a moment after a merge moved the base: an unknown state is asked again before it is taken for a clean one
        for (var attempt = 0; attempt < UnknownReads && read is { Merged: false, Merge: MergeState.Unknown }; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.BotRebaseUnknownWaitSeconds)), time, cancellationToken);
            read = await facts.ReadAsync(entry.Repositories[0], number, cancellationToken);
        }
        return PullRequestFreshness.Of(entry, read, IsBot, IsUnsignedBot);
    }

    private bool IsBot(string? login) => login is not null && options.BotAuthors.Contains(login, StringComparer.OrdinalIgnoreCase);

    private bool IsUnsignedBot(string? login) => IsBot(login) && options.BotUnsignedAuthors.Contains(login, StringComparer.OrdinalIgnoreCase);

    /// <summary>What becomes of a problem that waits in the line, from the verdict on its pull request. True when it is ready to start (nothing else of the lot is read in this pass).</summary>
    internal async Task<bool> ApplyInLineAsync(PlanEntry entry, FreshnessVerdict verdict, CancellationToken cancellationToken)
    {
        var id = entry.ProblemId;
        switch (verdict.Kind)
        {
            case Freshness.Left:
                _ = line.Remove(id);
                _ = parking.Release(id);
                _ = await audit.RecordAsync(MissionAuditEvents.QueuedProblemRefused, id, $"left the line without starting: {verdict.Reason}", CancellationToken.None);
                await PutAsideAsync(entry, verdict.Reason ?? "the pull request moved", cancellationToken);
                return false;
            case Freshness.Superseded:
                _ = line.Remove(id);
                _ = parking.Release(id);
                await DoneByAnotherAsync(entry, verdict, cancellationToken);
                return false;
            case Freshness.NeedsRebase:
                // A new head is the bot answering: the wait starts again from it (a bot that keeps up with a base that keeps moving is not put aside for it)
                // (the requests made over the life of the problem are kept: BotRebaseRequestsInAll is never passed)
                var before = parking.Get(id);
                if (verdict.Head is { } newer)
                {
                    _ = line.Replace(id, entry with { HeadSha = newer });
                    before = before is null ? null : before with { Since = time.GetUtcNow(), Requests = 0, LastRequested = null, LotWasBusy = false };
                }
                await ParkAsync(entry, verdict.Reason ?? "it is behind its base branch", verdict.Author, before, cancellationToken);
                return false;
            case Freshness.Followed:
                _ = line.Replace(id, entry with { HeadSha = verdict.Head });
                _ = parking.Release(id);
                await FollowedAsync(id, verdict.Head, cancellationToken);
                return true;
            default:
                _ = parking.Release(id);
                return true;
        }
    }

    /// <summary>The bot made the pull request again on a new head: the work follows it, and the trail says so.</summary>
    internal async Task FollowedAsync(string problemId, string? head, CancellationToken cancellationToken) =>
        _ = await audit.RecordAsync(MissionAuditEvents.ProblemFollowedBot, problemId, $"the bot made the pull request again: the work follows its new head {Short(head)}", cancellationToken);

    /// <summary>The bot closed its pull request because another change brought what it carried: nothing is left to do, and the problem is done by another operation.</summary>
    internal async Task DoneByAnotherAsync(PlanEntry entry, FreshnessVerdict verdict, CancellationToken cancellationToken)
    {
        _ = await audit.RecordAsync(MissionAuditEvents.ProblemClosed, entry.ProblemId, $"nothing left to do: {verdict.Reason}", CancellationToken.None);
        await SettleBeforeStartAsync(entry, ProblemState.Closed, $"done by another operation: {verdict.Reason}", cancellationToken);
    }

    // The pull request is behind its base or in conflict: the bot is asked to rebase it with its own command (Missions:BotRebaseCommands), at most BotRebaseRequests times in one wait and BotRebaseRequestsInAll in the life of the
    // problem, and after BotRebaseWaitMinutes without a new head the problem is put aside with the reason. A bot that has no command is not waited for
    internal async Task ParkAsync(PlanEntry entry, string reason, string? author, ParkedProblem? before, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var id = entry.ProblemId;
        if (author is null || !options.BotRebaseCommands.TryGetValue(author, out var command) || string.IsNullOrWhiteSpace(command))
        {
            await PutAsideBeforeStartAsync(entry, $"its bot ({author ?? "unknown"}) has no command to ask it to rebase the pull request ({reason})", cancellationToken);
            return;
        }
        // While another operation of its lot held the lot, the clock of the bot did not run and the base moved (that operation was merged): the wait starts again from now (ADR 0055)
        if (before is { LotWasBusy: true })
        {
            before = before with { Since = now, Requests = 0, LastRequested = null, LotWasBusy = false };
        }
        var since = before?.Since ?? now;
        if (now - since >= TimeSpan.FromMinutes(Math.Max(1, options.BotRebaseWaitMinutes)))
        {
            await PutAsideBeforeStartAsync(entry, $"the bot did not rebase the pull request within {options.BotRebaseWaitMinutes} minutes ({reason})", cancellationToken);
            return;
        }
        var requests = before?.Requests ?? 0;
        var all = before?.AllRequests ?? 0;
        var lastRequested = before?.LastRequested;
        var due = lastRequested is null || now - lastRequested >= TimeSpan.FromMinutes(Math.Max(1, options.BotRebaseWaitMinutes) / (double)Math.Max(1, options.BotRebaseRequests));
        if (requests < options.BotRebaseRequests && all < options.BotRebaseRequestsInAll && due && entry.PullRequest is { } number && services.GetService<IForgeActs>() is { } acts)
        {
            var asked = await acts.RequestBotRebaseAsync(entry.Repositories[0], number, command, cancellationToken);
            _ = await audit.RecordAsync(MissionAuditEvents.BotRebaseRequested, id, asked ? $"asked the bot to rebase the pull request ({reason})" : $"the forge did not take the request to rebase the pull request ({reason})", CancellationToken.None);
            requests++;
            all++;
            lastRequested = now;
        }
        if (before is null)
        {
            _ = await audit.RecordAsync(MissionAuditEvents.ProblemParked, id, $"waits for its bot to rebase the pull request, the lot goes on: {reason}", CancellationToken.None);
        }
        parking.Set(id, new ParkedProblem(since, requests, now, lastRequested, reason, all));
    }

    internal async Task PutAsideBeforeStartAsync(PlanEntry entry, string why, CancellationToken cancellationToken)
    {
        _ = line.Remove(entry.ProblemId);
        _ = parking.Release(entry.ProblemId);
        _ = await audit.RecordAsync(MissionAuditEvents.QueuedProblemRefused, entry.ProblemId, $"put aside: {why}", CancellationToken.None);
        await PutAsideAsync(entry, why, cancellationToken);
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
        if (read is null || read.Merged || read.Closed || !IsBot(read.Author) || !PullRequestFreshness.IsTheBotsOwn(read, IsUnsignedBot(read.Author)))
        {
            return;
        }
        var gate = locks.Of(ProblemStarter.GateKey(entry.Repositories[0]));
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

    // A problem that leaves the line without having started is **settled in the flow, with its reason**, so that the maintainer sees it where he follows the work (a failed problem put aside, or one that another operation did), not only in the audit trail
    internal Task PutAsideAsync(PlanEntry entry, string reason, CancellationToken cancellationToken) =>
        SettleBeforeStartAsync(entry, ProblemState.Failed, $"put aside before it started: {reason}", cancellationToken);

    internal async Task SettleBeforeStartAsync(PlanEntry entry, ProblemState state, string note, CancellationToken cancellationToken)
    {
        if (services.GetService<SandboxApprovals>() is { } approvals && await approvals.PrepareAsync(entry, cancellationToken) && approvals.For(entry) is { } approval)
        {
            problems.Save(new ProblemRun(entry.ProblemId, entry.ApplicationId, state, time.GetUtcNow(), entry, entry.MaxWorld, true, approval, Note: note));
        }
    }

    private static string Short(string? sha) => sha is { Length: > 8 } ? sha[..8] : sha ?? string.Empty;
}
