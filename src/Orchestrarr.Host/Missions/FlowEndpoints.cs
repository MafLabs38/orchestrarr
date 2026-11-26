using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Approvals;
using Orchestrarr.Core.Accounts;
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

public sealed record PriorityRequest(int Priority);

/// <summary>
/// The Queue screen (ADR 0056): the dispatch list, and the operator's two levers on the line, the priority and the hold. Each is an act of a person who may approve, written in the audit trail and pushed live to the screens. Neither grants
/// a power: a priority orders the work, a hold delays it.
/// </summary>
internal static class FlowEndpoints
{
    public static void MapFlowEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/flow/queue", (IServiceProvider services, IMissionStore missions, IMandateLedger mandates, IProblemStore problems, MissionProgress progress, RunnerRegistry runners) =>
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
                var state = "waitingTurn";
                if (held is not null)
                {
                    state = "held";
                }
                else if (parked)
                {
                    state = "waitingBot";
                }
                entries.Add(new DispatchEntry(index + 1, id, shown?.Title ?? waiting.Entry.Objective, waiting.Entry.Brief?.Kind, repository, waiting.Priority, RouteRank.Of(waiting.Entry), waiting.QueuedAt, state,
                    shown?.Activity?.Waiting?.Subject, held, next));
            }
            var inProcess = board.Where(problem => problem.State == "inProgress")
                .Select(problem => new InProcessEntry(problem.ProblemId, problem.Title, problem.Kind, problem.Repository, problem.Steps.FirstOrDefault(step => step.State is "running" or "reading" or "waiting")?.World ?? string.Empty, problem.Priority))
                .ToList();
            return Results.Ok(new DispatchView(entries, inProcess));
        }).RequireAuthorization(Permissions.FleetRead);

        // A hold: an operation of the line does not start until it is released, and never holds its lot (ADR 0056). A reason in words is required
        api.MapPost("/flow/{problemId}/hold", async (string problemId, HoldRequest request, HttpContext context, IApproverAccessor accessor, IServiceProvider services, IMissionAudit audit, ILiveNotifier live, TimeProvider time) =>
        {
            if (Operator(accessor, context) is { Refusal: { } refusal })
            {
                return refusal;
            }
            var by = accessor.Current(context)!.Subject;
            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > ProblemHolds.MaxReason)
            {
                return Results.Json(new ErrorResponse("holdNeedsAReason"), statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            if (services.GetService<ProblemQueue>() is not { } line || services.GetService<ProblemHolds>() is not { } holds || !line.Contains(problemId))
            {
                return Results.Json(new ErrorResponse("notInTheLine"), statusCode: StatusCodes.Status409Conflict);
            }
            var reason = request.Reason.ReplaceLineEndings(" ").Trim();
            holds.Hold(problemId, new HeldProblem(by, time.GetUtcNow(), reason));
            _ = await audit.RecordAsync(MissionAuditEvents.ProblemHeld, problemId, $"held by {by}: {reason}", CancellationToken.None);
            live.Publish(LiveTopics.Missions);
            return Results.Ok();
        }).RequireAuthorization(Permissions.ApprovalsCreate);

        api.MapPost("/flow/{problemId}/release", async (string problemId, HttpContext context, IApproverAccessor accessor, IServiceProvider services, IMissionAudit audit, ILiveNotifier live) =>
        {
            if (Operator(accessor, context) is { Refusal: { } refusal })
            {
                return refusal;
            }
            if (services.GetService<ProblemHolds>() is not { } holds || !holds.Release(problemId))
            {
                return Results.Json(new ErrorResponse("notHeld"), statusCode: StatusCodes.Status409Conflict);
            }
            _ = await audit.RecordAsync(MissionAuditEvents.ProblemReleased, problemId, $"released by {accessor.Current(context)!.Subject}", CancellationToken.None);
            live.Publish(LiveTopics.Missions);
            return Results.Ok();
        }).RequireAuthorization(Permissions.ApprovalsCreate);

        // A priority set by a person, from 0 to 100 (ADR 0050, ADR 0056): an operation of the line, or one in process
        api.MapPost("/flow/{problemId}/priority", async (string problemId, PriorityRequest request, HttpContext context, IApproverAccessor accessor, IServiceProvider services, ProblemConductor conductor, IMissionAudit audit, ILiveNotifier live, CancellationToken ct) =>
        {
            if (Operator(accessor, context) is { Refusal: { } refusal })
            {
                return refusal;
            }
            if (request.Priority is < FlowPriority.Lowest or > FlowPriority.Highest)
            {
                return Results.Json(new ErrorResponse("priorityOutOfRange"), statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            var by = accessor.Current(context)!.Subject;
            if (services.GetService<ProblemQueue>() is { } line && line.SetPriority(problemId, request.Priority))
            {
                _ = await audit.RecordAsync(MissionAuditEvents.PrioritySet, problemId, $"priority in the line set to {request.Priority}, by {by}", CancellationToken.None);
            }
            else if (!(await conductor.SetPriorityAsync(problemId, request.Priority, by, ct)).Ok)
            {
                return Results.Json(new ErrorResponse("notInTheFlow"), statusCode: StatusCodes.Status409Conflict);
            }
            live.Publish(LiveTopics.Missions);
            return Results.Ok();
        }).RequireAuthorization(Permissions.ApprovalsCreate);
    }

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
