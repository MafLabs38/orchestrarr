using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Applications;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.WorkItems;
using Orchestrarr.Host.Approvals;
using Orchestrarr.Host.Tasks;

namespace Orchestrarr.Host.Decisions;

/// <summary>The table as the screen reads it: one row per problem, how far each can go, and the version of the descriptions it was computed from.</summary>
public sealed record ProblemsReport(DateTimeOffset? ReadAt, string SourceKind, bool Stale, SourceError? Error, string DescriptionsVersion, IReadOnlyList<ProblemRow> Rows);

/// <summary><c>GoesUpTo</c> is how far the row went when the operator saw it: the approval is for that, and for no more.</summary>
public sealed record SelectedRow(string? Id, string? HeadSha, Ceiling? GoesUpTo, bool StopBeforeProduction);

public sealed record ApproveProblemsRequest(List<SelectedRow>? Rows, string? SecondFactorCode, string? DescriptionsVersion);

/// <summary>
/// The table of decisions (ADR 0019). The client sends which rows it selected and at which commit; <b>the plan is computed here again</b>, from the
/// current table, so that nothing the client says can widen what a row may do.
/// </summary>
internal static class ProblemsEndpoints
{
    public static void MapProblemsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/problems", async (WorkItemCatalog catalog, IApplicationCatalog applications, ExecutorRegistry executors, bool? refresh, CancellationToken ct) =>
        {
            var report = await catalog.GetAsync(refresh ?? false, ct);
            return Results.Ok(new ProblemsReport(report.ReadAt, report.SourceKind, report.Stale, report.Error, applications.Version, DecisionTable.Build(report.Items, applications, Acceptance(executors))));
        }).RequireAuthorization(Permissions.FleetRead);

        api.MapPost("/problems/approve", async (ApproveProblemsRequest request, HttpContext context, IApproverAccessor accessor, WorkItemCatalog catalog, IApplicationCatalog applications, ExecutorRegistry executors, ApprovalService service, CancellationToken ct) =>
        {
            var (approver, refusal) = ApprovalEndpoints.Authorize(accessor, context);
            if (approver is null)
            {
                return refusal!;
            }

            var report = await catalog.GetAsync(refresh: false, ct);
            var table = DecisionTable.Build(report.Items, applications, Acceptance(executors)).ToDictionary(row => row.Id, StringComparer.Ordinal);
            // What the operator saw is what is approved: the rule lives once, in Core
            var resolution = DecisionSelection.Resolve(table, [.. (request.Rows ?? []).Select(row => new ChosenRow(row.Id, row.HeadSha, row.GoesUpTo, row.StopBeforeProduction))], request.DescriptionsVersion, applications.Version);
            if (resolution.Refused)
            {
                await service.TraceRefusalAsync(approver, resolution.Mismatch?.WorkItemId ?? string.Empty, ApprovalRefusal.PlanChanged);
                return ApprovalEndpoints.RefusalResult(ApprovalRefusal.PlanChanged);
            }

            var result = await service.ApproveSelectionAsync(approver, resolution.Selection, request.SecondFactorCode, ct);
            return result.Refusal is { } code
                ? ApprovalEndpoints.RefusalResult(code)
                : Results.Json(new ApproveResponse([.. result.Approvals.Select(ApprovalEndpoints.ToDto)]), statusCode: StatusCodes.Status201Created);
        }).RequireAuthorization(Permissions.ApprovalsCreate).RequireRateLimiting(ApprovalsServiceCollectionExtensions.ApprovingPolicy);
    }

    // Only the executors that asked for work lately: one that is gone promises nothing
    private static List<ExecutorAcceptance> Acceptance(ExecutorRegistry executors) =>
        [.. executors.Snapshot().Where(executor => executor.Connected).Select(executor => new ExecutorAcceptance(executor.Kinds, executor.Repositories))];
}
