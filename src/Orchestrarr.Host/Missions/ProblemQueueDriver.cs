using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Live;

namespace Orchestrarr.Host.Missions;

/// <summary>
/// Starts the problems that wait in the line of a repository, one at a time: a problem starts when no other problem is in progress on its repository, whatever became of the one before (done, failed, escalated or stopped). The
/// problem was validated and planned already; what is checked at the start (the stop, the approval, the powers, the staging) is checked now, on the state of that moment, and a problem that cannot start leaves the line with
/// its reason in the trail.
/// </summary>
internal sealed class ProblemQueueDriver(ProblemQueue line, IProblemStore problems, IServiceProvider services, ProblemConductor conductor, MissionProgress progress, IMissionAudit audit, ILiveNotifier live, TimeProvider time,
    ILogger<ProblemQueueDriver> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await PassAsync(stoppingToken);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    logger.LogWarning(failure, "A pass of the line of problems failed; the next one tries again");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping
        }
    }

    internal async Task PassAsync(CancellationToken cancellationToken)
    {
        if (line.Waiting().Count == 0)
        {
            return;
        }
        if (services.GetService<SandboxApprovals>() is not { } approvals)
        {
            return;
        }
        foreach (var ready in line.TakeReady(repository => PlanEndpoints.BusyRepository(problems, repository)))
        {
            var result = await PlanEndpoints.StartAsync(new PlanEndpoints.Prepared(ready.RowId, ready.Entry, null), approvals, conductor, progress, services.GetService<RepairRegistry>(), cancellationToken);
            if (result.Refusal is { } refusal)
            {
                _ = await audit.RecordAsync(MissionAuditEvents.QueuedProblemRefused, ready.Entry.ProblemId, $"left the line without starting: {refusal}", CancellationToken.None);
            }
            live.Publish(LiveTopics.Missions);
        }
    }
}
