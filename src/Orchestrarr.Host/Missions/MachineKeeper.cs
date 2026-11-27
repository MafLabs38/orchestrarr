using Orchestrarr.Core.Machines;
using Orchestrarr.Host.Live;

namespace Orchestrarr.Host.Missions;

/// <summary>
/// Keeps the pool of lab machines alive (ADR 0047): at start every configured machine goes to be qualified (it is Idle only after a qualification by this control plane), a change of state is pushed to the screens, and
/// a lease whose runner gave no sign of life for too long ends, so that no machine is held for ever by a runner that has gone.
/// </summary>
internal sealed class MachineKeeper(MachinePool pool, MissionsOptions options, ILiveNotifier live, IServiceProvider services, ILogger<MachineKeeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        pool.Changed += machine => live.Publish(LiveTopics.Machines, machine.Id);
        foreach (var setting in options.Machines)
        {
            pool.Register(setting.Id, setting.Role);
        }
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    foreach (var machine in pool.ExpireLeases())
                    {
                        logger.LogWarning("The lease of {Machine} expired: its runner gave no sign of life", machine.Id);
                    }
                    // Never while the global stop is engaged: a stop stops the machines too
                    if (services.GetService<Orchestrarr.Core.Missions.IStopSignal>() is not { Engaged: true })
                    {
                        foreach (var machine in pool.RequalifyAfterFault(TimeSpan.FromMinutes(Math.Max(1, options.MachineRequalifyMinutes)), Math.Max(0, options.MachineRequalifyPerHour), options.MachineRequalifyNever))
                        {
                            logger.LogWarning("{Machine} was down: it is qualified again by itself", machine.Id);
                            if (services.GetService<Orchestrarr.Core.Missions.IMissionAudit>() is { } audit)
                            {
                                _ = await audit.RecordAsync("machine.requalified", machine.Id, $"qualified again by itself: {machine.Reason}", CancellationToken.None);
                            }
                        }
                    }
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    logger.LogWarning(failure, "The expiry of the leases failed; the next pass tries again");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping
        }
    }
}
