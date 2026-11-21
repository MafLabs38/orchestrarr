using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.MissionRunner;

/// <summary>Asks the control plane for a mission of its own, says that it starts, works, reports and hands the dossier over, then asks again.</summary>
public sealed partial class MissionRunnerWorker(IMissionControlPlane controlPlane, StagingProof staging, MissionRunnerOptions options, ILogger<MissionRunnerWorker> logger) : BackgroundService
{
    // How many reads of the stop in a row may fail before the work is stopped (about half a minute with the default poll)
    private const int MaxUnreadStops = 6;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await TakeOneAsync(stoppingToken))
                {
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
        if (await controlPlane.ClaimAsync(cancellationToken) is not { } mission)
        {
            return false;
        }
        if (!string.Equals(mission.World, options.World, StringComparison.OrdinalIgnoreCase))
        {
            await controlPlane.ProgressAsync(mission.MissionId, $"This runner serves the {options.World} world, not {mission.World}: nothing done.", cancellationToken);
            return false;
        }
        if (!await controlPlane.StartedAsync(mission.MissionId, cancellationToken))
        {
            return false;
        }
        // The global stop reaches the work that is going on: it is read while the work runs, and the work is cancelled (the machine is still restored)
        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watcher = WatchStopAsync(work);
        try
        {
            var dossier = await staging.RunAsync(mission, line => controlPlane.ProgressAsync(mission.MissionId, line, cancellationToken), work.Token);
            if (!await controlPlane.HandOverAsync(mission.MissionId, dossier, cancellationToken))
            {
                LogRefused(mission.MissionId);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await controlPlane.ProgressAsync(mission.MissionId, "The global stop is engaged: the work was stopped and nothing is handed over.", cancellationToken);
        }
        finally
        {
            await work.CancelAsync();
            await watcher;
        }
        return true;
    }

    private async Task WatchStopAsync(CancellationTokenSource work)
    {
        try
        {
            var unread = 0;
            while (!work.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.StopPollSeconds)), work.Token);
                var engaged = await ReadStopAsync(work.Token);
                unread = engaged is null ? unread + 1 : 0;
                // A stop that has not been readable for a while is a stop that cannot be confirmed: the work does not go on blind
                if (engaged is true || unread >= MaxUnreadStops)
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
