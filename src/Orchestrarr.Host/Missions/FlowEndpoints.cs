using System.Globalization;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Approvals;
using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Host.Tasks;
using Orchestrarr.Host.Live;

namespace Orchestrarr.Host.Missions;

/// <summary>One operation of the dispatch list (ADR 0056): where it is in the order of the line, what it is, its priority and rank, since when it waits, why, and the hold of a person if there is one.</summary>
public sealed record DispatchEntry(int Position, string ProblemId, string Title, string? Kind, string Repository, int Priority, int Rank, DateTimeOffset Since, string State, string? Detail, HeldProblem? Held, bool Next);

/// <summary>An operation in process, whose priority a person can set: it orders the next mission a runner is given.</summary>
public sealed record InProcessEntry(string ProblemId, string Title, string? Kind, string Repository, string Place, int Priority);

/// <summary>The dispatch list: the line in the order it goes, and the operations in process.</summary>
public sealed record DispatchView(IReadOnlyList<DispatchEntry> Line, IReadOnlyList<InProcessEntry> InProcess);

public sealed record HoldRequest(string? Reason);

public sealed record PriorityRequest(int? Priority);

/// <summary>
/// The Queue screen (ADR 0056): the dispatch list, and the operator's two levers on the line, the priority and the hold. Each is an act of a person who may approve, written in the audit trail and pushed live to the screens. Neither grants
/// a power: a priority orders the work, a hold delays it.
/// </summary>
internal static class FlowEndpoints
{
    private const int MaxTravelerSteps = 500;

    public static void MapFlowEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/flow/queue", (IServiceProvider services, IMissionStore missions, IMandateLedger mandates, IProblemStore problems, MissionProgress progress, RunnerRegistry runners) => MissionEndpoints.WhenReadable(() =>
        {
            var line = services.GetService<ProblemQueue>();
            var holds = services.GetService<ProblemHolds>();
            var flow = MissionEndpoints.FlowOf(services, missions.Recent(MissionEndpoints.MaxPageSize), mandates, problems, progress, runners).DistinctBy(problem => problem.ProblemId).ToDictionary(problem => problem.ProblemId, StringComparer.Ordinal);
            var board = flow.Values.ToList();
            var ordered = line?.Ordered() ?? [];
            // The first of each lot that can start is the next to go when its lot is free
            var nextOfLot = new HashSet<string>(StringComparer.Ordinal);
            var entries = new List<DispatchEntry>();
            foreach (var (waiting, index) in ordered.Select((waiting, index) => (waiting, index)))
            {
                var id = waiting.Entry.ProblemId;
                var held = holds?.Get(id);
                var parked = services.GetService<ProblemParking>()?.IsParked(id) ?? false;
                var repository = waiting.Entry.Repositories.FirstOrDefault() ?? string.Empty;
                var next = held is null && !parked && nextOfLot.Add(repository);
                var shown = flow.GetValueOrDefault(id);
                // The state is the one every screen reads (OperationStates); a problem that has just entered the line and is not in the flow yet waits for its turn
                var state = shown?.Operation ?? "waitingTurn";
                entries.Add(new DispatchEntry(index + 1, id, shown?.Title ?? waiting.Entry.Objective, waiting.Entry.Brief?.Kind, repository, waiting.Priority, RouteRank.Of(waiting.Entry), waiting.QueuedAt, state,
                    shown?.Activity?.Waiting?.Subject, held, next));
            }
            var inProcess = board.Where(problem => problem.State == "inProgress")
                .Select(problem => new InProcessEntry(problem.ProblemId, problem.Title, problem.Kind, problem.Repository, OperationStates.CurrentStep(problem.Steps)?.World ?? FlowBoard.Queue, problem.Priority))
                .ToList();
            return Results.Ok(new DispatchView(entries, inProcess));
        })).RequireAuthorization(Permissions.FleetRead);

        // The traveler of an operation (ADR 0056): its steps one after the other, from the audit trail
        api.MapGet("/problems/{problemId}/traveler", (string problemId, IServiceProvider services, IMissionStore missions, IMandateLedger mandates, IProblemStore problems, MissionProgress progress, RunnerRegistry runners, IAuditLog audit) => MissionEndpoints.WhenReadable(() =>
        {
            // What the flow shows of it now; an operation older than what the flow lists is told from what the store keeps of it (its steps are in the audit trail all the same)
            var shown = MissionEndpoints.FlowOf(services, missions.Recent(MissionEndpoints.MaxPageSize), mandates, problems, progress, runners).FirstOrDefault(problem => string.Equals(problem.ProblemId, problemId, StringComparison.Ordinal))
                ?? (problems.Get(problemId) is { } run
                    ? new FlowProblem(run.ProblemId, FlowView.TitleOf(run.Entry), run.Entry.Brief?.Kind ?? run.Entry.ProblemClass, run.Entry.Repositories.FirstOrDefault() ?? string.Empty, FlowView.StateOf(run.State), run.Priority, run.StartedAt, [], null, run.Note)
                    : null);
            if (shown is null)
            {
                return Results.NotFound();
            }
            var ofProblem = missions.ForProblem(problemId);
            var rows = audit.About([problemId, .. ofProblem.Select(mission => mission.Id)], MaxTravelerSteps);
            return Results.Ok(Traveler.Build(shown, ofProblem, rows));
        })).RequireAuthorization(Permissions.AuditRead);

        // A hold: an operation of the line does not start until it is released, and never holds its lot (ADR 0056). A reason in words is required
        api.MapPost("/flow/{problemId}/hold", async (string problemId, HoldRequest request, HttpContext context, IApproverAccessor accessor, IServiceProvider services, IMissionAudit audit, ILiveNotifier live, TimeProvider time) =>
        {
            var (approver, refusal) = Operator(accessor, context);
            if (approver is null)
            {
                return refusal ?? Results.Forbid();
            }
            var by = WhoIs(context, approver);
            // The reason goes into the append-only trail: no line break, no control character and no format character (a bidi override would make the line read otherwise)
            var reason = request.Reason is { } said
                ? new string([.. said.ReplaceLineEndings(" ").Where(character => !char.IsControl(character) && CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.Format)]).Trim()
                : string.Empty;
            if (reason.Length is 0 or > ProblemHolds.MaxReason)
            {
                return Results.Json(new ErrorResponse("holdNeedsAReason"), statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            if (services.GetService<ProblemQueue>() is not { } line || services.GetService<ProblemHolds>() is not { } holds || !line.Contains(problemId))
            {
                return Results.Json(new ErrorResponse("notInTheLine"), statusCode: StatusCodes.Status409Conflict);
            }
            // Every act of a person is written before it is made (a full audit trail of every act): a hold the trail did not take is never made
            if (!await audit.RecordAsync(MissionAuditEvents.ProblemHeld, problemId, $"held by {by}: {reason}", CancellationToken.None))
            {
                return Unrecorded();
            }
            // Held in the same step as it is found in the line: an operation that started since it was written is not held, and the trail says so
            if (!holds.Hold(problemId, new HeldProblem(by, time.GetUtcNow(), reason)))
            {
                _ = await audit.RecordAsync(MissionAuditEvents.ActNotMade, problemId, "the hold was not made: the operation had left the line", CancellationToken.None);
                return Results.Json(new ErrorResponse("notInTheLine"), statusCode: StatusCodes.Status409Conflict);
            }
            live.Publish(LiveTopics.Missions);
            return Results.Ok();
        }).RequireAuthorization(Permissions.ApprovalsCreate);

        api.MapPost("/flow/{problemId}/release", async (string problemId, HttpContext context, IApproverAccessor accessor, IServiceProvider services, IMissionAudit audit, ILiveNotifier live) =>
        {
            var (approver, refusal) = Operator(accessor, context);
            if (approver is null)
            {
                return refusal ?? Results.Forbid();
            }
            if (services.GetService<ProblemHolds>() is not { } holds || !holds.IsHeld(problemId))
            {
                return Results.Json(new ErrorResponse("notHeld"), statusCode: StatusCodes.Status409Conflict);
            }
            // Written before it is released: a start could take the operation as soon as it is released, so a release that the trail did not take is never made
            if (!await audit.RecordAsync(MissionAuditEvents.ProblemReleased, problemId, $"released by {WhoIs(context, approver)}", CancellationToken.None))
            {
                return Unrecorded();
            }
            _ = holds.Release(problemId);
            live.Publish(LiveTopics.Missions);
            return Results.Ok();
        }).RequireAuthorization(Permissions.ApprovalsCreate);

        // A priority set by a person, from 0 to 100 (ADR 0050, ADR 0056): an operation of the line, or one in process
        api.MapPost("/flow/{problemId}/priority", async (string problemId, PriorityRequest request, HttpContext context, IApproverAccessor accessor, IServiceProvider services, ProblemConductor conductor, IMissionAudit audit, ILiveNotifier live, CancellationToken ct) =>
        {
            var (approver, refusal) = Operator(accessor, context);
            if (approver is null)
            {
                return refusal ?? Results.Forbid();
            }
            if (request.Priority is not { } priority || priority is < FlowPriority.Lowest or > FlowPriority.Highest)
            {
                return Results.Json(new ErrorResponse("priorityOutOfRange"), statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            var by = WhoIs(context, approver);
            var line = services.GetService<ProblemQueue>();
            // Written before it is made, as every act of a person: a priority the trail did not take is never set
            if (line is not null && line.Contains(problemId))
            {
                if (!await audit.RecordAsync(MissionAuditEvents.PrioritySet, problemId, $"priority in the line set to {priority}, by {by}", CancellationToken.None))
                {
                    return Unrecorded();
                }
                if (line.SetPriority(problemId, priority))
                {
                    live.Publish(LiveTopics.Missions);
                    return Results.Ok();
                }
                // it left the line since it was written: the trail says so, and the priority goes to the problem in progress, which writes its own line
                _ = await audit.RecordAsync(MissionAuditEvents.ActNotMade, problemId, "the priority was not set in the line: the operation had left it", CancellationToken.None);
            }
            if (await conductor.SetPriorityAsync(problemId, priority, by, ct) is { Ok: false } refused)
            {
                return refused.Refusal == ProblemConductor.Unrecorded ? Unrecorded() : Results.Json(new ErrorResponse("notInTheFlow"), statusCode: StatusCodes.Status409Conflict);
            }
            live.Publish(LiveTopics.Missions);
            return Results.Ok();
        }).RequireAuthorization(Permissions.ApprovalsCreate);
    }

    // Who acted, as a person reads it: the name of the account, else its identifier
    private static string WhoIs(HttpContext context, Approver approver) =>
        context.User.Identity?.Name is { Length: > 0 } name ? name : approver.Subject;

    // The trail could not take the act: it is undone and said so
    private static IResult Unrecorded() => Results.Json(new ErrorResponse(ApiErrors.StorageUnavailable), statusCode: StatusCodes.Status503ServiceUnavailable);

    // The same gate as approving: a person who may approve
    private static (Approver? Approver, IResult? Refusal) Operator(IApproverAccessor accessor, HttpContext context)
    {
        var (approver, refusal) = ApprovalEndpoints.Authorize(accessor, context);
        if (approver is null)
        {
            return (null, refusal);
        }
        return approver is { IsPerson: true, IsOperator: true } ? (approver, null) : (null, Results.Json(new ErrorResponse(ApiErrors.NotAnOperator), statusCode: StatusCodes.Status403Forbidden));
    }
}
