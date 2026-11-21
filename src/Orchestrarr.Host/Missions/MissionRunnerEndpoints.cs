using Orchestrarr.Core.Missions;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Host.Live;

namespace Orchestrarr.Host.Missions;

/// <summary>A mission that a runner may take: what to do, where, and until when (the mandate itself stays with the control plane in this first version).</summary>
public sealed record ClaimedMission(string MissionId, string ProblemId, string ApplicationId, string World, string Objective, string Repository, int? PullRequest, string? HeadSha, DateTimeOffset MandateExpiresAt,
    IReadOnlyList<string> Acts);

public sealed record MissionClaimResponse(ClaimedMission? Mission);

public sealed record MissionProgressRequest(string? Line);

public sealed record MissionHandOverRequest(string? Claim, string? WhatChangedAndWhy, List<string>? EvidenceDigests, List<string>? OpenPoints, bool Complex, string? ComplexReason, string? Commit);

/// <summary>What a mission shows while it runs: the lines the runner reported, in the order they came. Kept in memory (the dossier is the record), and bounded.</summary>
public sealed class MissionProgress
{
    private const int MaxLines = 300;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> _lines = new(StringComparer.Ordinal);

    public void Add(string missionId, string line)
    {
        var list = _lines.GetOrAdd(missionId, _ => []);
        lock (list)
        {
            list.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} {(line.Length > 500 ? line[..500] : line)}");
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
        group.MapPost("/missions/claim", (ClaimRequest? request, HttpContext context, IMissionStore missions, IMandateLedger mandates, IProblemStore problems, TimeProvider time, Orchestrarr.Host.Tasks.RunnerRegistry registry) =>
        {
            var name = runnerName(context);
            if (request?.Hello is { } hello)
            {
                // A runner of missions is seen like the others: what it accepts decides what the table offers
                registry.Seen(name, hello);
            }
            var mission = missions.Recent(200).Where(candidate => candidate.State == MissionState.Mandated && MandateOf(candidate, mandates) is { } signed && signed.Mandate.RunnerId == name && signed.Mandate.ExpiresAt > time.GetUtcNow())
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
            if (!Owns(id, runnerName(context), missions, mandates) || string.IsNullOrWhiteSpace(request.Line))
            {
                return Results.NotFound();
            }
            progress.Add(id, request.Line);
            live.Publish(LiveTopics.Missions, id);
            return Results.Ok();
        });

        group.MapPost("/missions/{id}/handover", async (string id, MissionHandOverRequest request, HttpContext context, IMissionStore missions, IMandateLedger mandates, ProblemConductor conductor, ILiveNotifier live, CancellationToken ct) =>
        {
            if (!Owns(id, runnerName(context), missions, mandates) || string.IsNullOrWhiteSpace(request.Claim) || string.IsNullOrWhiteSpace(request.WhatChangedAndWhy))
            {
                return Results.NotFound();
            }
            var dossier = new Dossier(id, request.Claim, request.WhatChangedAndWhy, [.. request.EvidenceDigests ?? []], [.. request.OpenPoints ?? []], request.Complex, request.ComplexReason, request.Commit);
            var result = await conductor.HandOverAsync(id, dossier, ct);
            live.Publish(LiveTopics.Missions, id);
            return result.Ok ? Results.Ok() : Results.Conflict(new { error = result.Refusal });
        });
    }

    private static SignedMandate? MandateOf(Mission mission, IMandateLedger mandates) => mission.MandateId is { } id ? mandates.Get(id) : null;

    private static bool Owns(string id, string runner, IMissionStore missions, IMandateLedger mandates) =>
        missions.Get(id) is { } mission && MandateOf(mission, mandates) is { } signed && signed.Mandate.RunnerId == runner;
}
