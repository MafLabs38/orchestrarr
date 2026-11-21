using System.Security.Cryptography;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Agents;
using Orchestrarr.Host.Storage;
using Orchestrarr.Storage;

namespace Orchestrarr.Host.Missions;

/// <summary>
/// Ends what no runner will say for itself, on a timer: the global stop engaged, a mandate that expired without a runner, a problem past its time bound. It starts nothing, so the stop does not hold it back.
/// </summary>
internal sealed class MissionSweeper(ProblemConductor conductor, ILogger<MissionSweeper> logger, TimeProvider time) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    _ = await conductor.SweepAsync(stoppingToken);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    logger.LogWarning(failure, "The sweep of the missions failed; the next pass will try again");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping
        }
    }
}
