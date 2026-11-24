using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Missions;

/// <summary>A row that is ready to start (its plan entry with the problem as read and the lead's plan), or why it will not start: one of the two, never both.</summary>
internal sealed record PreparedProblem(string RowId, PlanEntry? Entry, ValidatedProblem? Refused);

/// <summary>
/// Starts the problems of the test bed, **one work site at a time on a repository** (ADR 0049): the decision "start now, or wait in line" and the start itself are made under one gate per repository, by the endpoint that validates and by the
/// driver that takes the line, so that two requests, or a request and a pass of the driver, never both find the repository free. A problem that waits is planned and not started: it has no mandate and uses no approval until it starts.
/// </summary>
internal sealed class ProblemStarter(IServiceProvider services, IProblemStore problems, ProblemQueue line, ProblemLocks locks, ProblemConductor conductor, MissionProgress progress, IMissionAudit audit, IStopSignal stop, TimeProvider time)
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
            // A problem already in progress is the conductor's to refuse ("already being worked on"), not a reason to queue it again
            if (problems.Get(entry.ProblemId) is { State: ProblemState.InProgress } || (!line.Waits(repository) && !Busy(repository)))
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
                foreach (var ready in line.TakeReady(other => !string.Equals(other, repository, StringComparison.Ordinal) || Busy(other)))
                {
                    await StartQueuedAsync(ready, cancellationToken);
                }
            }
            finally
            {
                _ = gate.Release();
            }
        }
    }

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
