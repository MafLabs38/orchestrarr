using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Machines;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Approvals;
using Orchestrarr.Host.Live;

namespace Orchestrarr.Host.Missions;

public sealed record MachineCheckView(string Name, bool Passed, string Detail);

/// <summary>What a machine did over the window, as for equipment of a fab: the share of the time it was not down (availability), the share a mission held it (utilisation), the lots, the repairs.</summary>
public sealed record MachineStatsView(DateTimeOffset From, DateTimeOffset To, double Availability, double Utilisation, int Lots, double MeanLotSeconds, int DownEvents, double MeanTimeToRepairSeconds, IReadOnlyDictionary<string, double> SecondsInState);

public sealed record MachineView(string Id, string Role, string State, DateTimeOffset Since, string? MissionId, string? Reason, string Runner, DateTimeOffset? QualifiedAt, bool? QualificationPassed,
    IReadOnlyList<MachineCheckView> Checks, MachineStatsView Stats);

public sealed record MachinesResponse(IReadOnlyList<MachineView> Machines, int WindowHours);

/// <summary>What a runner says about a machine: the mission that holds it, and the checks of a qualification it made.</summary>
public sealed record MachineLeaseRequest(string? MissionId, string? Reason = null);

public sealed record QualificationRequest(List<MachineCheckView>? Checks);

/// <summary>
/// The lab machines (ADR 0047): what the screens read (the state of each and its statistics, live on the <c>machines</c> topic), the request for a new qualification, and the routes of the runner that works on a machine
/// (it holds a lease while a mission runs, says it is alive, gives the machine back, and reports the qualification that it made). A runner touches only the machines that the configuration gives it.
/// </summary>
internal static class MachineEndpoints
{
    private const int MaxChecks = 20;
    private const int MaxText = 300;
    private const int DefaultWindowHours = 24;
    private const int MaxWindowHours = 24 * 30;

    public static void MapMachineEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/machines", (int? hours, IServiceProvider services, TimeProvider time) =>
        {
            if (services.GetService<MachinePool>() is not { } pool)
            {
                return Results.Ok(new MachinesResponse([], 0));
            }
            var options = services.GetRequiredService<MissionsOptions>();
            var window = Math.Clamp(hours ?? DefaultWindowHours, 1, MaxWindowHours);
            var now = time.GetUtcNow();
            var machines = pool.All().Select(machine => ViewOf(machine, options, pool.Transitions(machine.Id, now.AddHours(-window)), now, window)).ToList();
            return Results.Ok(new MachinesResponse(machines, window));
        }).RequireAuthorization(Permissions.FleetRead);

        // A machine that is Down, or Finished and not taken up, is put to qualify again: by a person, once what was wrong is repaired
        api.MapPost("/machines/{id}/requalify", (string id, HttpContext context, IApproverAccessor accessor, IServiceProvider services) =>
        {
            var (approver, refusal) = ApprovalEndpoints.Authorize(accessor, context);
            if (approver is null)
            {
                return refusal!;
            }
            if (!approver.IsPerson || !approver.IsOperator)
            {
                return Results.Json(new ErrorResponse(ApiErrors.NotAnOperator), statusCode: StatusCodes.Status403Forbidden);
            }
            if (services.GetService<MachinePool>() is not { } pool)
            {
                return Results.NotFound();
            }
            // A machine is put out of service only by a failure: a person asks again for one that is down or finished, never for one that is idle or in process
            if (pool.Get(id) is not { State: MachineState.Down or MachineState.Finished })
            {
                return Results.Json(new ErrorResponse("notDownOrFinished"), statusCode: StatusCodes.Status409Conflict);
            }
            var result = pool.BeginQualification(id, $"a qualification asked by {approver.Subject}");
            return result.Ok ? Results.Ok(new { state = result.Machine!.State.ToString() }) : Results.Json(new ErrorResponse(result.Refusal ?? "refused"), statusCode: StatusCodes.Status409Conflict);
        }).RequireAuthorization(Permissions.ApprovalsCreate);
    }

    /// <summary>The routes of a runner, under its own token: its machines only.</summary>
    public static void MapMachineRunnerRoutes(this RouteGroupBuilder group, Func<HttpContext, string> runnerName)
    {
        group.MapGet("/machines/{id}", (string id, HttpContext context, IServiceProvider services) =>
            Owned(context, id, services, runnerName) is { } pool ? Results.Ok(StateOf(pool.Get(id)!)) : Results.NotFound());

        group.MapPost("/machines/{id}/heartbeat", (string id, MachineLeaseRequest? request, HttpContext context, IServiceProvider services) =>
            Owned(context, id, services, runnerName) is { } pool && request?.MissionId is { Length: > 0 } mission
                ? Results.Ok(new { ok = pool.Heartbeat(id, mission) })
                : Results.NotFound());

        group.MapPost("/machines/{id}/release", (string id, MachineLeaseRequest? request, HttpContext context, IServiceProvider services) =>
        {
            if (Owned(context, id, services, runnerName) is not { } pool || request?.MissionId is not { Length: > 0 } mission)
            {
                return Results.NotFound();
            }
            var result = pool.Release(id, mission, Cut(request.Reason));
            return Results.Ok(new { ok = result.Ok, refusal = result.Refusal });
        });

        // The runner starts the restore and the checks of a machine that is Finished (or that a person asked to qualify again)
        group.MapPost("/machines/{id}/qualifying", (string id, HttpContext context, IServiceProvider services) =>
        {
            if (Owned(context, id, services, runnerName) is not { } pool)
            {
                return Results.NotFound();
            }
            var result = pool.BeginQualification(id, "the machine is being restored and checked");
            return Results.Ok(new { ok = result.Ok, refusal = result.Refusal });
        });

        group.MapPost("/machines/{id}/qualification", (string id, QualificationRequest? request, HttpContext context, IServiceProvider services, TimeProvider time) =>
        {
            if (Owned(context, id, services, runnerName) is not { } pool)
            {
                return Results.NotFound();
            }
            var checks = (request?.Checks ?? []).Take(MaxChecks).Select(check => new MachineCheck(Cut(check.Name) ?? "check", check.Passed, Cut(check.Detail) ?? string.Empty)).ToList();
            var result = pool.Report(id, new Qualification(time.GetUtcNow(), checks));
            return Results.Ok(new { ok = result.Ok, refusal = result.Refusal, state = result.Machine?.State.ToString() });
        });
    }

    // The pool, when the machine exists and the configuration gives it to this runner
    private static MachinePool? Owned(HttpContext context, string id, IServiceProvider services, Func<HttpContext, string> runnerName)
    {
        if (services.GetService<MachinePool>() is not { } pool || pool.Get(id) is null)
        {
            return null;
        }
        var setting = services.GetRequiredService<MissionsOptions>().Machines.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
        return setting is not null && string.Equals(setting.Runner, runnerName(context), StringComparison.Ordinal) ? pool : null;
    }

    /// <summary>Takes the lease of the machine for the mission, if the machine is the runner's, of the role of the world, and Idle. <c>Taken</c>: the lease is new (a claim repeated by the holder keeps what it has).</summary>
    internal static (bool Leased, bool Taken) TryLeaseFor(MachinePool pool, MissionsOptions options, string? machine, string runner, Mission mission)
    {
        var role = mission.World == World.Staging ? MachineRole.Staging : MachineRole.Release;
        var setting = options.Machines.FirstOrDefault(candidate => string.Equals(candidate.Id, machine, StringComparison.Ordinal));
        if (setting is null || setting.Role != role || !string.Equals(setting.Runner, runner, StringComparison.Ordinal))
        {
            return (false, false);
        }
        var result = pool.TryLease(setting.Id, mission.Id);
        return (result.Ok, result.Taken);
    }

    private static object StateOf(Machine machine) => new { state = machine.State.ToString(), role = machine.Role.ToString(), missionId = machine.MissionId };

    private static string? Cut(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        return text.Length <= MaxText ? text : text[..MaxText];
    }

    private static MachineView ViewOf(Machine machine, MissionsOptions options, IReadOnlyList<MachineTransition> transitions, DateTimeOffset now, int hours)
    {
        var stats = MachineMetrics.Compute(transitions, now.AddHours(-hours), now);
        var qualification = machine.LastQualification;
        return new MachineView(
            machine.Id, machine.Role.ToString(), machine.State.ToString(), machine.Since, machine.MissionId, machine.Reason,
            options.Machines.FirstOrDefault(candidate => candidate.Id == machine.Id)?.Runner ?? string.Empty, qualification?.At, qualification?.Passed,
            [.. (qualification?.Checks ?? []).Select(check => new MachineCheckView(check.Name, check.Passed, check.Detail))],
            new MachineStatsView(stats.From, stats.To, stats.Availability, stats.Utilisation, stats.Lots, stats.MeanLot.TotalSeconds, stats.DownEvents, stats.MeanTimeToRepair.TotalSeconds,
                stats.InState.ToDictionary(entry => entry.Key.ToString(), entry => entry.Value.TotalSeconds)));
    }
}
