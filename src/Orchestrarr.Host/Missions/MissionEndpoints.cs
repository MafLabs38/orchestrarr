using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Approvals;

namespace Orchestrarr.Host.Missions;

/// <summary>A mandate as a person sees it: who issued it, for which runner, which acts, until when. The signature is not shown.</summary>
public sealed record MandateView(string Id, string RunnerId, string Source, IReadOnlyList<string> Acts, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, bool Revoked);

public sealed record DossierView(string Claim, string WhatChangedAndWhy, IReadOnlyList<string> OpenPoints, bool Complex, string? ComplexReason);

/// <summary>A mission with its mandate, its dossier and the state of its problem.</summary>
public sealed record MissionView(
    string Id, string ProblemId, string ApplicationId, string World, string Objective, string State, string? End, string? EndReason, int SendBacks, string? ProblemState, MandateView? Mandate, DossierView? Dossier);

/// <summary>What a person sees of the missions (FUNCTIONAL 5.12): the list and one mission. Nothing here changes anything.</summary>
internal static class MissionEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    public static void MapMissionEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/missions", (IMissionStore missions, IMandateLedger mandates, IProblemStore problems, IDossierStore dossiers, int? limit) =>
            WhenReadable(() => Results.Ok(Views(missions.Recent(Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize)), mandates, problems, dossiers)))).RequireAuthorization(Permissions.FleetRead);
        api.MapGet("/missions/{id}", (string id, IMissionStore missions, IMandateLedger mandates, IProblemStore problems, IDossierStore dossiers) =>
            WhenReadable(() => missions.Get(id) is { } mission ? Results.Ok(View(mission, mandates, problems, dossiers)) : Results.NotFound())).RequireAuthorization(Permissions.FleetRead);
    }

    internal static MissionView View(Mission mission, IMandateLedger mandates, IProblemStore problems, IDossierStore dossiers) => Views([mission], mandates, problems, dossiers)[0];

    // A page of missions is read with one read of each store, not one read of each store per mission
    internal static IReadOnlyList<MissionView> Views(IReadOnlyList<Mission> list, IMandateLedger mandates, IProblemStore problems, IDossierStore dossiers)
    {
        var mandateIds = list.Select(mission => mission.MandateId).OfType<string>().ToList();
        var signedById = mandates.GetMany(mandateIds);
        var revoked = mandates.RevokedAmong(mandateIds);
        var problemById = problems.GetMany([.. list.Select(mission => mission.ProblemId)]);
        var dossierById = dossiers.GetMany([.. list.Select(mission => mission.Id)]);
        return [.. list.Select(mission =>
        {
            var mandate = mission.MandateId is { } id && signedById.TryGetValue(id, out var signed)
                ? new MandateView(signed.Mandate.Id, signed.Mandate.RunnerId, signed.Mandate.Source.ToString(), [.. signed.Mandate.Acts.Select(act => act.ToString())], signed.Mandate.IssuedAt, signed.Mandate.ExpiresAt, revoked.Contains(id))
                : null;
            var dossier = dossierById.TryGetValue(mission.Id, out var found) ? new DossierView(found.Claim, found.WhatChangedAndWhy, found.OpenPoints, found.Complex, found.ComplexReason) : null;
            return new MissionView(mission.Id, mission.ProblemId, mission.ApplicationId, mission.World.ToString(), mission.Objective, mission.State.ToString(), mission.End?.ToString(), mission.EndReason, mission.SendBacks,
                problemById.TryGetValue(mission.ProblemId, out var run) ? run.State.ToString() : null, mandate, dossier);
        })];
    }

    private static IResult WhenReadable(Func<IResult> read)
    {
        try
        {
            return read();
        }
        catch (StorageUnavailableException)
        {
            return Results.Json(new ErrorResponse(ApiErrors.StorageUnavailable), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
