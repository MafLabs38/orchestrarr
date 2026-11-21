using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Orchestrarr.MissionRunner;

/// <summary>Asks the control plane for a mission of its own, says that it starts, works, reports and hands the dossier over, then asks again.</summary>
public sealed partial class MissionRunnerWorker(IMissionControlPlane controlPlane, StagingProof staging, MissionRunnerOptions options, ILogger<MissionRunnerWorker> logger) : BackgroundService
{
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
            catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or InvalidOperationException)
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
        var dossier = await staging.RunAsync(mission, line => controlPlane.ProgressAsync(mission.MissionId, line, cancellationToken), cancellationToken);
        if (!await controlPlane.HandOverAsync(mission.MissionId, dossier, cancellationToken))
        {
            LogRefused(mission.MissionId);
        }
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mission runner: {Message}")]
    private partial void LogFailure(string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The control plane did not accept the dossier of mission {MissionId}")]
    private partial void LogRefused(string missionId);
}
