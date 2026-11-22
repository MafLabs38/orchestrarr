using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.MissionRunner;

/// <summary>Asks the control plane for a mission of its own, says that it starts, works, reports and hands the dossier over, then asks again.</summary>
public sealed partial class MissionRunnerWorker(IMissionControlPlane controlPlane, IEnumerable<IWorldWork> works, MissionRunnerOptions options, ILogger<MissionRunnerWorker> logger, MachineQualifier? qualifier = null) : BackgroundService
{
    private const int HandOverAttempts = 3;
    private static readonly string[] WorldOrder = ["Staging", "Git", "Release"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await TakeOneAsync(stoppingToken))
                {
                    await MaintainMachinesAsync(stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                LogFailure(failure.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(options.PollSeconds, 10)), stoppingToken);
            }
        }
    }

    /// <summary>One mission, if there is one. True when work was done.</summary>
    internal async Task<bool> TakeOneAsync(CancellationToken cancellationToken)
    {
        // The worlds it serves, the later ones first; a world it has no work for is not asked about
        IWorldWork? work = null;
        ClaimedMission? mission = null;
        string? machine = null;
        foreach (var candidate in works.Where(known => options.Worlds.Contains(known.World, StringComparer.OrdinalIgnoreCase)).OrderByDescending(known => Array.IndexOf(WorldOrder, known.World)))
        {
            var candidateMachine = MachineOf(candidate.World);
            var offered = candidateMachine is null ? await controlPlane.ClaimAsync(candidate.World, cancellationToken) : await controlPlane.ClaimAsync(candidate.World, candidateMachine, cancellationToken);
            if (offered is { } claimed)
            {
                (work, mission, machine) = (candidate, claimed, candidateMachine);
                break;
            }
        }
        if (work is null || mission is null)
        {
            return false;
        }
        // The machine is worked on only if the control plane says that this mission holds it: a control plane that does not know the machine, or that gave it to another, is not a reason to work on what the last job left
        if (machine is not null && !await HoldsLeaseAsync(machine, mission.MissionId, cancellationToken))
        {
            LogFailure($"the control plane did not confirm that mission {mission.MissionId} holds {machine}: nothing is done on it");
            return false;
        }
        try
        {
            // A mission that was sent back is already started: it goes on, told what the controller asked
            if (mission.Rework)
            {
                await controlPlane.ProgressAsync(mission.MissionId, $"Sent back by the controller: {mission.Remarks ?? "no remarks"}", cancellationToken);
            }
            else if (!await controlPlane.StartedAsync(mission.MissionId, cancellationToken))
            {
                return false;
            }
            // The global stop reaches the work that is going on: it is read while the work runs, and the work is cancelled (the machine is still restored)
            using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var watcher = WatchStopAsync(stopped, machine, mission.MissionId);
            try
            {
                var dossier = await work.RunAsync(mission, line => controlPlane.ProgressAsync(mission.MissionId, line, cancellationToken), stopped.Token);
                if (!await HandOverAsync(mission.MissionId, dossier, cancellationToken))
                {
                    LogRefused(mission.MissionId);
                }
            }
            // Only the stop ends the work like this: a call that timed out is not a stop
            catch (OperationCanceledException) when (stopped.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                await controlPlane.ProgressAsync(mission.MissionId, "The global stop is engaged: the work was stopped and nothing is handed over.", cancellationToken);
            }
            finally
            {
                await stopped.CancelAsync();
                await watcher;
            }
            return true;
        }
        finally
        {
            // The mission is over, in any way: the machine is given back, then restored and checked before it can take another job (ADR 0047)
            if (machine is not null)
            {
                await GiveBackAsync(machine, mission.MissionId, cancellationToken);
            }
        }
    }

    // A proof that took minutes is not lost to one network error: the hand-over is tried a few times (a refusal of the control plane is final)
    private async Task<bool> HandOverAsync(string missionId, MissionHandOver dossier, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt < HandOverAttempts; attempt++)
        {
            try
            {
                return await controlPlane.HandOverAsync(missionId, dossier, cancellationToken);
            }
            catch (Exception failure) when (failure is HttpRequestException || (failure is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                LogFailure(failure.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds)), cancellationToken);
            }
        }
        // The last attempt is not caught: the loop of the runner says why it failed
        return await controlPlane.HandOverAsync(missionId, dossier, cancellationToken);
    }

    private async Task WatchStopAsync(CancellationTokenSource work, string? machine, string missionId)
    {
        try
        {
            var unread = 0;
            while (!work.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.StopPollSeconds)), work.Token);
                if (!await HeartbeatAsync(machine, missionId, work.Token))
                {
                    LogFailure($"the lease of {machine} is gone: the work of mission {missionId} stops");
                    await work.CancelAsync();
                }
                var engaged = await ReadStopAsync(work.Token);
                unread = engaged is null ? unread + 1 : 0;
                // A stop that has not been readable for a while is a stop that cannot be confirmed: the work does not go on blind
                if (engaged is true || unread >= Math.Max(1, options.StopReadFailuresAllowed))
                {
                    await work.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The work ended, or the runner is stopping
        }
    }

    private async Task<bool> HoldsLeaseAsync(string machine, string missionId, CancellationToken cancellationToken) =>
        await controlPlane.MachineStateAsync(machine, cancellationToken) is { State: "InProcess" } state && string.Equals(state.MissionId, missionId, StringComparison.Ordinal);

    // The machine that a world works on: the staging machine for the staging world, the deployment machine for the release world, none for the others
    private string? MachineOf(string world) => world switch
    {
        "Staging" => options.StagingMachine,
        "Release" => options.DeployMachine,
        _ => null,
    };

    // A sign of life while the work goes on, so that the lease is not taken for the lease of a runner that has gone; a heartbeat that fails is lost, not a reason to stop
    // Returns false only when the control plane says that the mission no longer holds the machine: the work must stop then (nothing can be handed over safely from a machine that is not ours any more)
    private async Task<bool> HeartbeatAsync(string? machine, string missionId, CancellationToken cancellationToken)
    {
        if (machine is null)
        {
            return true;
        }
        try
        {
            return await controlPlane.MachineHeartbeatAsync(machine, missionId, cancellationToken);
        }
        catch (Exception failure) when (failure is HttpRequestException || (failure is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogFailure(failure.Message);
            return true;
        }
    }

    // The mission is over: give the machine back, then qualify it. The give-back is not cancelled with the runner (it is short and it must be said); a qualification that is cut short is made again when the runner starts
    private async Task GiveBackAsync(string machine, string missionId, CancellationToken cancellationToken)
    {
        try
        {
            await controlPlane.MachineReleaseAsync(machine, missionId, null, CancellationToken.None);
            await QualifyAsync(machine, cancellationToken);
        }
        catch (Exception failure) when (!IsShutdown(failure, cancellationToken))
        {
            // The give-back runs in a finally: nothing it meets may hide what the mission ended with. A machine that was not given back or qualified is taken up again by the maintenance of the idle runner
            LogFailure($"{machine}: {failure.Message}");
        }
    }

    // A failure that is the runner stopping is not swallowed; any other is a failure of one operation
    private static bool IsShutdown(Exception failure, CancellationToken cancellationToken) => failure is OperationCanceledException && cancellationToken.IsCancellationRequested;

    // The restore and the checks of one machine, and what they found told to the control plane: Idle if every check passed, Down with why if one failed
    private async Task QualifyAsync(string machine, CancellationToken cancellationToken)
    {
        if (qualifier is null)
        {
            return;
        }
        await controlPlane.MachineQualifyingAsync(machine, cancellationToken);
        var checks = await qualifier.QualifyAsync(machine, restore: string.Equals(machine, options.StagingMachine, StringComparison.Ordinal), cancellationToken);
        await controlPlane.MachineQualifiedAsync(machine, checks, cancellationToken);
    }

    // When the runner has no mission: a machine of its own that is Finished or Qualifying (the control plane started, a person asked, a mission ended while the runner was down) is restored and checked; a machine that is
    // InProcess is held by a mission that this runner is no longer working on, because it works on one mission at a time and is idle now: the lease is given back
    internal async Task MaintainMachinesAsync(CancellationToken cancellationToken)
    {
        foreach (var machine in new[] { options.StagingMachine, options.DeployMachine }.Where(known => !string.IsNullOrWhiteSpace(known)).Distinct(StringComparer.Ordinal).Select(known => known!))
        {
            // One machine that fails is told and does not keep the other from being maintained
            try
            {
                await MaintainAsync(machine, cancellationToken);
            }
            catch (Exception failure) when (!IsShutdown(failure, cancellationToken))
            {
                LogFailure($"{machine}: {failure.Message}");
            }
        }
    }

    private async Task MaintainAsync(string machine, CancellationToken cancellationToken)
    {
        var served = options.Worlds.Contains(machine == options.StagingMachine ? "Staging" : "Release", StringComparer.OrdinalIgnoreCase);
        if (!served || await controlPlane.MachineStateAsync(machine, cancellationToken) is not { } state)
        {
            return;
        }
        if (state.State == "InProcess" && state.MissionId is { } held)
        {
            await controlPlane.MachineReleaseAsync(machine, held, "the runner that held the machine is no longer working on it", cancellationToken);
        }
        else if (state.State is "Finished" or "Qualifying")
        {
            await QualifyAsync(machine, cancellationToken);
        }
    }

    // A stop that cannot be read now is read again at the next turn: the work is not stopped for a failed read
    private async Task<bool?> ReadStopAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await controlPlane.StopEngagedAsync(cancellationToken);
        }
        catch (Exception failure) when (failure is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException || (failure is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogFailure(failure.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mission runner: {Message}")]
    private partial void LogFailure(string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The control plane did not accept the dossier of mission {MissionId}")]
    private partial void LogRefused(string missionId);
}
