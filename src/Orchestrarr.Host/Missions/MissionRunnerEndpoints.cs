using Orchestrarr.Core.Missions;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Host.Live;

namespace Orchestrarr.Host.Missions;

/// <summary>What a mission shows while it runs: the lines the runner reported, in the order they came. Kept in memory (the dossier is the record), bounded in lines per mission and in missions kept.</summary>
public sealed class MissionProgress(TimeProvider time)
{
    private const int MaxLines = 300;
    private const int MaxMissions = 200;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> _lines = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _order = new();

    public void Add(string missionId, string line)
    {
        var list = _lines.GetOrAdd(missionId, id =>
        {
            _order.Enqueue(id);
            // The oldest missions are forgotten first: what a screen shows of an old mission is its dossier, not these lines
            while (_lines.Count > MaxMissions && _order.TryDequeue(out var oldest))
            {
                _lines.TryRemove(oldest, out _);
            }
            return [];
        });
        lock (list)
        {
            list.Add($"{time.GetUtcNow():HH:mm:ss} {(line.Length > 500 ? line[..500] : line)}");
            if (list.Count > MaxLines)
            {
                list.RemoveAt(0);
            }
        }
    }

    public IReadOnlyList<string> Lines(string missionId)
    {
        if (!_lines.TryGetValue(missionId, out var list))
        {
            return [];
        }
        lock (list)
        {
            return [.. list];
        }
    }
}

/// <summary>
/// The routes of a runner that serves missions (ADR 0031, 0033), under the token of the runner like the others: it asks for a mission of its own, says that it started, reports what it does and hands the dossier over.
/// A mission is a runner's only if its mandate names that runner, whatever the runner asks.
/// </summary>
internal static class MissionRunnerEndpoints
{
    public static void MapMissionRunnerRoutes(this RouteGroupBuilder group, Func<HttpContext, string> runnerName)
    {
        group.MapPost("/missions/claim", (ClaimRequest? request, string? world, HttpContext context, IMissionStore missions, IMandateLedger mandates, IProblemStore problems, TimeProvider time, Orchestrarr.Host.Tasks.RunnerRegistry registry) =>
        {
            var name = runnerName(context);
            if (request?.Hello is { } hello)
            {
                // A runner of missions is seen like the others: what it accepts decides what the table offers
                registry.Seen(name, hello);
            }
            var mission = missions.Recent(200).Where(candidate => candidate.State == MissionState.Mandated && MandateOf(candidate, mandates) is { } signed && signed.Mandate.RunnerId == name && signed.Mandate.ExpiresAt > time.GetUtcNow() && (world is null || string.Equals(candidate.World.ToString(), world, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(candidate => candidate.Id, StringComparer.Ordinal).FirstOrDefault();
            if (mission is null || MandateOf(mission, mandates) is not { } mandate || problems.Get(mission.ProblemId)?.Entry is not { } entry)
            {
                return Results.Ok(new MissionClaimResponse(null));
            }
            return Results.Ok(new MissionClaimResponse(new ClaimedMission(mission.Id, mission.ProblemId, mission.ApplicationId, mission.World.ToString(), mission.Objective, entry.Repositories[0], entry.PullRequest, entry.HeadSha,
                mandate.Mandate.ExpiresAt, [.. mandate.Mandate.Acts.Select(act => act.ToString())])));
        });

        group.MapPost("/missions/{id}/started", async (string id, HttpContext context, IMissionStore missions, IMandateLedger mandates, ProblemConductor conductor, ILiveNotifier live, CancellationToken ct) =>
        {
            if (!Owns(id, runnerName(context), missions, mandates))
            {
                return Results.NotFound();
            }
            var result = await conductor.StartedAsync(id, ct);
            live.Publish(LiveTopics.Missions, id);
            return result.Ok ? Results.Ok() : Results.Conflict(new { error = result.Refusal });
        });

        group.MapPost("/missions/{id}/progress", (string id, MissionProgressRequest request, HttpContext context, IMissionStore missions, IMandateLedger mandates, MissionProgress progress, ILiveNotifier live) =>
        {
            if (!Owns(id, runnerName(context), missions, mandates) || string.IsNullOrWhiteSpace(request.Line) || missions.Get(id) is not { State: MissionState.Mandated or MissionState.Running })
            {
                return Results.NotFound();
            }
            progress.Add(id, request.Line);
            live.Publish(LiveTopics.Missions, id);
            return Results.Ok();
        });

        group.MapPost("/missions/{id}/handover", async (string id, MissionHandOver request, HttpContext context, IMissionStore missions, IMandateLedger mandates, ProblemConductor conductor, ILiveNotifier live, CancellationToken ct) =>
        {
            if (!Owns(id, runnerName(context), missions, mandates) || string.IsNullOrWhiteSpace(request.Claim) || string.IsNullOrWhiteSpace(request.WhatChangedAndWhy))
            {
                return Results.NotFound();
            }
            var dossier = new Dossier(id, request.Claim, request.WhatChangedAndWhy, Bounded(request.EvidenceDigests), Bounded(request.OpenPoints), request.Complex, request.ComplexReason, request.Commit);
            var result = await conductor.HandOverAsync(id, dossier, ct);
            live.Publish(LiveTopics.Missions, id);
            return result.Ok ? Results.Ok() : Results.Conflict(new { error = result.Refusal });
        });
    }

    // What a runner may put in a dossier is bounded here as it is in the prompt of the controller: a runner that is not trusted cannot fill the store
    private const int MaxListed = 20;
    private const int MaxTextLength = 2000;

    private static List<string> Bounded(IReadOnlyList<string>? values) =>
        [.. (values ?? []).Take(MaxListed).Select(value => value.Length > MaxTextLength ? value[..MaxTextLength] : value)];

    private static SignedMandate? MandateOf(Mission mission, IMandateLedger mandates) => mission.MandateId is { } id ? mandates.Get(id) : null;

    private static bool Owns(string id, string runner, IMissionStore missions, IMandateLedger mandates) =>
        missions.Get(id) is { } mission && MandateOf(mission, mandates) is { } signed && signed.Mandate.RunnerId == runner;
}
