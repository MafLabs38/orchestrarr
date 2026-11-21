using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Live;

namespace Orchestrarr.Host.Missions;

/// <summary>
/// Drives the crossings (FUNCTIONAL 4, steps 5 and 7): when a mission has handed over, the controller agent reads the dossier next to the facts the control plane fetched itself, and its decision is applied by the
/// conductor, which makes its own checks first and can refuse a grant that the controller gave. A controller that cannot answer is never a grant: the mission waits and is asked again later.
/// </summary>
internal sealed class MissionDriver(IMissionStore missions, IDossierStore dossiers, IFactsSource facts, ControllerAgent controller, ProblemConductor conductor, MissionProgress progress, ILiveNotifier live, TimeProvider time,
    ILogger<MissionDriver> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(45);
    private readonly Dictionary<string, DateTimeOffset> _askedAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deployRecorded = new(StringComparer.Ordinal);

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
                    logger.LogWarning(failure, "A pass of the crossings failed; the next one tries again");
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
        foreach (var mission in missions.Recent(100).Where(candidate => candidate.State == MissionState.HandingOver))
        {
            if (_askedAt.TryGetValue(mission.Id, out var at) && time.GetUtcNow() - at < Retry)
            {
                continue;
            }
            _askedAt[mission.Id] = time.GetUtcNow();
            await DecideAsync(mission, cancellationToken);
        }
    }

    private async Task DecideAsync(Mission mission, CancellationToken cancellationToken)
    {
        if (dossiers.Get(mission.Id) is not { } dossier)
        {
            return;
        }
        progress.Add(mission.Id, "the controller reads the dossier and the facts");
        // A deployment is done by the runner: what it says it did is written down next to the authorisation, as what the runner reported and not as a fact of the control plane
        if (mission.World == World.Release && _deployRecorded.Add(mission.Id))
        {
            _ = await conductor.RecordActPerformedAsync(mission.Id, GatedAct.Deploy, $"reported by the runner: {(dossier.Claim.Length > 300 ? dossier.Claim[..300] : dossier.Claim)}", cancellationToken);
        }
        var observed = await facts.FactsAsync(new FactsQuery(mission.ProblemId, mission.Id, mission.World, mission.Frame, dossier, dossier.Commit), cancellationToken);
        var view = new FactsView(observed.ChangedPaths ?? [], $"checks of the head commit: {observed.CiGreenOnExactCommit?.ToString() ?? "not known"}", mission.World == World.Staging ? "not applicable in the staging world" : observed.Health ?? "not read", []);
        var outcome = await controller.DecideAsync(new ControllerInput(mission, dossier, view), cancellationToken);
        if (outcome.Decision is not { } decision)
        {
            progress.Add(mission.Id, $"the controller gave no decision ({outcome.Failure}); it will be asked again");
            live.Publish(LiveTopics.Missions, mission.Id);
            return;
        }
        if (decision.Kind == ControllerDecisionKind.SendBack && decision.Remarks is { } remarks)
        {
            progress.Remark(mission.Id, remarks);
        }
        progress.Add(mission.Id, $"the controller decided: {decision.Kind} ({string.Join(" | ", decision.Reasons)})");
        var result = await conductor.DecideAsync(mission.Id, decision, cancellationToken);
        var failed = result.Failures.Count > 0 ? $", its own checks failed: {string.Join(", ", result.Failures)}" : string.Empty;
        progress.Add(mission.Id, result.Ok || result.Moved ? $"the conductor applied it: {result.Mission?.World} {result.Mission?.State}{failed}" : $"the conductor refused: {result.Refusal}");
        // A mission that moved is not asked about again; one that the conductor refused stays handed over, and is asked again only after the retry delay (not at every pass)
        if (result.Ok || result.Moved)
        {
            _askedAt.Remove(mission.Id);
        }
        live.Publish(LiveTopics.Missions, mission.Id);
    }
}
