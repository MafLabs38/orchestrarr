using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Live;

namespace Orchestrarr.Host.Missions;

/// <summary>
/// Drives the crossings (FUNCTIONAL 4, steps 5 and 7): when a mission has handed over, the controller agent reads the dossier next to the facts the control plane fetched itself, and its decision is applied by the
/// conductor, which makes its own checks first and can refuse a grant that the controller gave. A controller that cannot answer is never a grant: the mission waits and is asked again later.
/// </summary>
internal sealed class MissionDriver(IMissionStore missions, IProblemStore problems, IDossierStore dossiers, IFactsSource facts, ControllerAgent controller, ProblemConductor conductor, MissionProgress progress, ILiveNotifier live, TimeProvider time,
    IServiceProvider services, ILogger<MissionDriver> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(45);
    private readonly Dictionary<string, DateTimeOffset> _askedAt = new(StringComparer.Ordinal);

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
        // A push credential outlives neither its mission nor the stop (ADR 0057)
        if (services.GetService<PushCredentialLedger>() is { } ledger)
        {
            await ledger.SweepAsync(cancellationToken);
        }
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

    // The worker of the staging world made the change and justified each change to a pipeline file (ADR 0057); the worlds after it merge and release that same change, so their controller reads the same justification. Read
    // from the staging dossier the controller accepted, never written into this world's dossier
    internal static Dossier WithStagingJustification(IMissionStore missions, IDossierStore dossiers, Mission mission, Dossier dossier)
    {
        if (mission.World == World.Staging || dossier.PipelineJustification is not null)
        {
            return dossier;
        }
        // The latest staging mission that was accepted is the one whose change this world carries (a fix forward makes a newer one)
        var staged = missions.ForProblem(mission.ProblemId).Where(other => other.World == World.Staging && other.State == MissionState.Accepted).Select(other => other.Id).ToList();
        var read = dossiers.GetMany(staged);
        var justification = staged.Select(id => read.GetValueOrDefault(id)?.PipelineJustification).LastOrDefault(text => text is not null);
        return justification is null ? dossier : dossier with { PipelineJustification = justification };
    }

    private async Task DecideAsync(Mission mission, CancellationToken cancellationToken)
    {
        if (dossiers.Get(mission.Id) is not { } dossier)
        {
            return;
        }
        // A refusal that no agent mends in this world (a conflict with the base, a pull request that moved) ends the problem at once, by the code: no question to the model, no send-back, and the next problem of the repository goes on.
        // The marker is a claim of the runner, and what it can do with it is to stop work, never to widen a power
        if (mission.World == World.Staging && MissionEvidence.RefusalOf(dossier.EvidenceDigests) is { } refusal)
        {
            await EndRefusedAsync(mission, refusal, cancellationToken);
            return;
        }
        progress.Add(mission.Id, "the controller reads the dossier and the facts");
        var observed = await facts.FactsAsync(new FactsQuery(mission.ProblemId, mission.Id, mission.World, mission.Frame, dossier, dossier.Commit), cancellationToken);
        var view = new FactsView(observed.ChangedPaths ?? [], $"checks of the head commit: {observed.CiGreenOnExactCommit?.ToString() ?? "not known"}", mission.World == World.Staging ? "not applicable in the staging world" : observed.Health ?? "not read", [], observed.ChangeReading);
        var outcome = await controller.DecideAsync(new ControllerInput(mission, WithStagingJustification(missions, dossiers, mission, dossier), view, problems.Get(mission.ProblemId)?.Entry.Brief), cancellationToken);
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

    private async Task EndRefusedAsync(Mission mission, string refusal, CancellationToken cancellationToken)
    {
        var why = refusal == MissionEvidence.RefusedConflict
            ? "the pull request conflicts with its base branch: it needs a new pull request (the bot rebases its own), and the work goes on with the next problem"
            : "the pull request moved since the maintainer approved it: a bot's goes back in the line on the commit it is now at, anyone else's waits for a new validation, and the work goes on with the next problem";
        progress.Add(mission.Id, $"the runner refused before building: {why}");
        var result = await conductor.EndRefusedAsync(mission.Id, why, cancellationToken);
        progress.Add(mission.Id, result.Ok || result.Moved ? $"the conductor ended the problem: {result.Mission?.World} {result.Mission?.State}" : $"the conductor refused: {result.Refusal}");
        if (result.Ok || result.Moved)
        {
            _askedAt.Remove(mission.Id);
            // A bot's pull request that was made again while it was proved goes back in the line on its new head (ADR 0055); anyone else's stays ended
            if (services.GetService<ProblemStarter>() is { } starter)
            {
                await starter.RequeueAfterRefusalAsync(mission.ProblemId, cancellationToken);
            }
        }
        live.Publish(LiveTopics.Missions, mission.Id);
    }
}
