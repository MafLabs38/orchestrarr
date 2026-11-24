using Orchestrarr.Host.Live;

namespace Orchestrarr.Host.Missions;

/// <summary>
/// Starts the problems that wait in the line of a repository, one at a time: a problem starts when no other problem is in progress on its repository, whatever became of the one before (done, failed, escalated or stopped). The rules are
/// those of <see cref="ProblemStarter"/>, which also empties the line on the global stop; this only gives it a beat.
/// </summary>
internal sealed class ProblemQueueDriver(ProblemStarter starter, ILiveNotifier live, TimeProvider time, ILogger<ProblemQueueDriver> logger) : BackgroundService
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
        await starter.StartReadyAsync(cancellationToken);
        live.Publish(LiveTopics.Missions);
    }
}
