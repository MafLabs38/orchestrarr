using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Core.WorkItems;
using Orchestrarr.Host.Approvals;
using Orchestrarr.Host.Tasks;
using Orchestrarr.Storage;

namespace Orchestrarr.Host.Decisions;

/// <summary>A line of the history as the screen reads it: the photograph, and what became of it.</summary>
public sealed record HistoryLine(
    long Id, HistoryState State, HistoryOutcome Outcome, string Application, ProblemKind Kind, string Description, string? Repository, int? PullRequest,
    string? HeadSha, Guid? BatchId, DateTimeOffset At, string By, string? Reason);

/// <summary>A page of the history, newest first. <c>Next</c> is the identifier to continue from, null at the end.</summary>
public sealed record HistoryReport(IReadOnlyList<HistoryLine> Lines, long? Next);

/// <summary>A row to set aside: its identifier and the description the operator saw (a skip is for that target, and comes back when the target changes).</summary>
public sealed record SkippedRow(string? Id, string? Description);

public sealed record SkipRequest(List<SkippedRow>? Rows, string? Reason);

public sealed record UnskipRequest(long? Id);

/// <summary>The history of what was decided, and the skip (ADR 0027). Reading needs the right to read the fleet, setting aside the right to approve.</summary>
internal static class ProblemHistoryEndpoints
{
    private const int DefaultPage = 100;
    private const int MaxPage = 200;
    private const int MaxReason = 300;
    private const int MaxSkipsAtOnce = 200;

    public static void MapProblemHistoryEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/problems/history", (IProblemHistory history, IApprovalStore approvals, IBatchQueue queue, string? state, int? limit, long? before) =>
        {
            if (state is not null && !Enum.TryParse<HistoryState>(state, ignoreCase: true, out _))
            {
                return Results.Json(new ErrorResponse("invalidRequest"), statusCode: StatusCodes.Status400BadRequest);
            }
            try
            {
                var page = history.Page(state is null ? null : Enum.Parse<HistoryState>(state, ignoreCase: true), Math.Clamp(limit ?? DefaultPage, 1, MaxPage), before);
                var byApproval = approvals.List(2000).ToDictionary(approval => approval.Id);
                var lines = page.Select(entry => Line(entry, byApproval, queue)).ToList();
                return Results.Ok(new HistoryReport(lines, page.Count == Math.Clamp(limit ?? DefaultPage, 1, MaxPage) ? page[^1].Id : null));
            }
            catch (StorageUnavailableException)
            {
                return Results.Json(new ErrorResponse(ApiErrors.StorageUnavailable), statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireAuthorization(Permissions.FleetRead);

        api.MapPost("/problems/skip", async (SkipRequest request, HttpContext context, IApproverAccessor accessor, WorkItemCatalog catalog, Orchestrarr.Core.Applications.IApplicationCatalog applications,
            RunnerRegistry runners, IBatchQueue queue, IProblemHistory history, IApprovalStore approvals, IAuditSink audit, TimeProvider time, CancellationToken ct) =>
        {
            var (approver, refusal) = ApprovalEndpoints.Authorize(accessor, context);
            if (approver is null)
            {
                return refusal!;
            }
            var chosen = request.Rows ?? [];
            if (chosen.Count is 0 or > MaxSkipsAtOnce || (request.Reason?.Length ?? 0) > MaxReason)
            {
                return Results.Json(new ErrorResponse("invalidRequest"), statusCode: StatusCodes.Status400BadRequest);
            }

            var report = await catalog.GetAsync(refresh: false, ct);
            var table = ProblemsEndpoints.VisibleTable(report, applications, runners, queue, history, approvals).ToDictionary(row => row.Id, StringComparer.Ordinal);
            var rows = new List<ProblemRow>();
            foreach (var one in chosen)
            {
                // What the operator saw is what is set aside: a row that is gone, or whose description changed since, is not skipped
                if (one.Id is null || !table.TryGetValue(one.Id, out var row) || !string.Equals(row.Description, one.Description, StringComparison.Ordinal))
                {
                    return Results.Json(new ErrorResponse("planChanged"), statusCode: StatusCodes.Status409Conflict);
                }
                rows.Add(row);
            }

            var now = time.GetUtcNow();
            var name = context.User.Identity?.Name ?? approver.Subject;
            try
            {
                foreach (var row in rows)
                {
                    // The trail comes first: a skip that cannot be traced is not made
                    if (!await audit.TryRecordAsync(new AuditEntry(now, AuditEvents.ProblemSkipped, approver.Subject, null, Short(row.Id)), ct))
                    {
                        return Results.Json(new ErrorResponse(ApiErrors.AuditUnavailable), statusCode: StatusCodes.Status503ServiceUnavailable);
                    }
                    history.Record(new HistoryEntry(0, row.Id, row.Description, HistoryState.Skipped, row.Application, row.Kind, row.Description, row.WorkItemId.StartsWith("repository:", StringComparison.Ordinal) ? row.Source : null,
                        row.PullRequest, row.HeadSha, null, null, now, name, request.Reason?.Trim()));
                }
            }
            catch (StorageUnavailableException)
            {
                return Results.Json(new ErrorResponse(ApiErrors.StorageUnavailable), statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            return Results.Json(new { skipped = rows.Count });
        }).RequireAuthorization(Permissions.ApprovalsCreate);

        api.MapPost("/problems/unskip", async (UnskipRequest request, HttpContext context, IApproverAccessor accessor, IProblemHistory history, IAuditSink audit, TimeProvider time, CancellationToken ct) =>
        {
            var (approver, refusal) = ApprovalEndpoints.Authorize(accessor, context);
            if (approver is null)
            {
                return refusal!;
            }
            if (request.Id is not { } id)
            {
                return Results.Json(new ErrorResponse("invalidRequest"), statusCode: StatusCodes.Status400BadRequest);
            }
            try
            {
                var now = time.GetUtcNow();
                if (!await audit.TryRecordAsync(new AuditEntry(now, AuditEvents.ProblemUnskipped, approver.Subject, null, id.ToString(System.Globalization.CultureInfo.InvariantCulture)), ct))
                {
                    return Results.Json(new ErrorResponse(ApiErrors.AuditUnavailable), statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                return history.UndoSkip(id, now) ? Results.NoContent() : Results.Json(new ErrorResponse("notFound"), statusCode: StatusCodes.Status404NotFound);
            }
            catch (StorageUnavailableException)
            {
                return Results.Json(new ErrorResponse(ApiErrors.StorageUnavailable), statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireAuthorization(Permissions.ApprovalsCreate);
    }

    // The identifier of a row can be long: the trail keeps the start, which names the problem
    private static string Short(string id) => id.Length > 120 ? id[..120] : id;

    private static HistoryLine Line(HistoryEntry entry, IReadOnlyDictionary<Guid, Approval> approvals, IBatchQueue queue)
    {
        var outcome = HistoryOutcome.Skipped;
        if (entry.State == HistoryState.Approved)
        {
            var approval = entry.ApprovalId is { } id && approvals.TryGetValue(id, out var found) ? found : null;
            IReadOnlyCollection<TaskResult?> results = [];
            if (approval?.State == ApprovalState.DryRunDone && entry.BatchId is { } batch && queue.Find(batch) is { } run)
            {
                results = [.. run.Tasks.Where(task => task.Spec.ApprovalId == entry.ApprovalId).Select(task => task.Result)];
            }
            outcome = HistoryOutcomes.Of(approval?.State, results);
        }
        return new HistoryLine(entry.Id, entry.State, outcome, entry.Application, entry.Kind, entry.Description, entry.Repository, entry.PullRequest, entry.HeadSha, entry.BatchId, entry.At, entry.By, entry.Reason);
    }
}
