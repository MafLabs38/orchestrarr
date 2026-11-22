using Orchestrarr.Core.Applications;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Infrastructure;
using Orchestrarr.Core.Missions;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Core.WorkItems;
using Orchestrarr.Host.Approvals;
using Orchestrarr.Host.Decisions;
using Orchestrarr.Host.Live;
using Orchestrarr.Host.Tasks;

namespace Orchestrarr.Host.Missions;

/// <summary>The rows the maintainer ticked, with the commit each one was seen at: what was seen is what is validated (the table is computed again here).</summary>
public sealed record ValidatePlanRequest(List<ValidatedRow>? Rows);

public sealed record ValidatedRow(string? Id, string? HeadSha, bool StopBeforeProduction = false);

/// <summary>What became of one ticked row: the problem it started and its first mission, or why nothing started.</summary>
public sealed record ValidatedProblem(string RowId, string? ProblemId, string? MissionId, string? World, string? Refusal);

public sealed record PlanStatus(bool Available);

public sealed record ValidatePlanResponse(IReadOnlyList<ValidatedProblem> Problems);

/// <summary>
/// The validation of the plan (FUNCTIONAL 5.2): the maintainer ticks problems of the table, and each one becomes a problem of the conductor, with its approval and the mandate of its first world. The table is built here again from what
/// was read last, so that nothing the client says can widen what a row may do. **Sandbox only for now**: without <c>Missions:Sandbox</c> there is no approval source that can give a certificate, and the route says so.
/// </summary>
internal static class PlanEndpoints
{
    // What a ticked row asks for: the plan goes as far as the last world, and the standing grant of the application decides how far it really goes (the intersection, read at each crossing)
    private static readonly World PlanCeiling = World.Release;
    private static readonly MissionBudget SandboxBudget = new(30, 3, 50);

    // A selection is a screenful, not a flood: more rows than the table can hold at once is refused
    private const int MaxRows = 400;

    public static void MapPlanEndpoints(this RouteGroupBuilder api)
    {
        // Whether the validation goes through the missions here: the screen sends the selection to the plan when it does, and says so
        api.MapGet("/plan", (IServiceProvider services) => Results.Ok(new PlanStatus(services.GetService<SandboxApprovals>() is not null))).RequireAuthorization(Permissions.FleetRead);
        api.MapPost("/plan/validate", async (ValidatePlanRequest request, HttpContext context, IApproverAccessor accessor, WorkItemCatalog catalog, IApplicationCatalog applications, RunnerRegistry runners, IBatchQueue queue,
            IProblemHistory history, IApprovalStore approvalStore, TimeProvider time, RelevancePolicy policy, InfrastructureCatalog infrastructure, ProblemConductor conductor, IServiceProvider services,
            LeadPlanner lead, MissionProgress progress, ILiveNotifier live, CancellationToken ct) =>
        {
            var (approver, refusal) = ApprovalEndpoints.Authorize(accessor, context);
            if (approver is null)
            {
                return refusal!;
            }
            if (services.GetService<SandboxApprovals>() is not { } approvals)
            {
                return Results.Json(new ErrorResponse("planNotAvailable"), statusCode: StatusCodes.Status501NotImplemented);
            }
            // The same gate as approving: a person, and an operator
            if (!approver.IsPerson || !approver.IsOperator)
            {
                return Results.Json(new ErrorResponse(ApiErrors.NotAnOperator), statusCode: StatusCodes.Status403Forbidden);
            }
            if ((request.Rows?.Count ?? 0) > MaxRows)
            {
                return Results.Json(new ErrorResponse("tooManyRows"), statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            var report = await catalog.GetAsync(refresh: false, ct);
            var table = ProblemsEndpoints.VisibleTable(TableInputs.Last(report, infrastructure), applications, runners, queue, history, approvalStore, time, policy).ToDictionary(row => row.Id, StringComparer.Ordinal);
            // The lead thinks about each problem first (two at a time), then the problems are started one after the other: nothing is started that the lead did not plan, and no approval is used for a problem that has no plan
            var prepared = await Task.WhenAll((request.Rows ?? []).Select(chosen => PrepareAsync(chosen, table, approver.Subject, lead, time, ct)));
            var started = new List<ValidatedProblem>();
            foreach (var one in prepared)
            {
                started.Add(await StartAsync(one, approvals, conductor, progress, ct));
            }
            live.Publish(LiveTopics.Missions);
            return Results.Json(new ValidatePlanResponse(started), statusCode: StatusCodes.Status201Created);
        }).RequireAuthorization(Permissions.ApprovalsCreate).RequireRateLimiting(ApprovalsServiceCollectionExtensions.ApprovingPolicy);
    }

    internal static World CeilingOf(ValidatedRow chosen) => chosen.StopBeforeProduction ? World.Git : PlanCeiling;

    // A row that is ready to start: its plan entry with the problem as read and the lead's plan, or why it will not start
    private sealed record Prepared(string RowId, PlanEntry? Entry, ValidatedProblem? Refused);

    private static async Task<Prepared> PrepareAsync(ValidatedRow chosen, Dictionary<string, ProblemRow> table, string approver, LeadPlanner lead, TimeProvider time, CancellationToken cancellationToken)
    {
        var id = chosen.Id ?? string.Empty;
        ValidatedProblem Refused(string reason, string? problemId = null) => new(id, problemId, null, null, reason);
        if (!table.TryGetValue(id, out var row))
        {
            return new Prepared(id, null, Refused("theTableChanged"));
        }
        if (row.HeadSha is not null && !string.Equals(row.HeadSha, chosen.HeadSha, StringComparison.Ordinal))
        {
            return new Prepared(id, null, Refused("thePullRequestMoved"));
        }
        // A row that nothing can act on is not started, and a stop before production that the operator ticked is carried into the plan: it narrows what the row asks for, never widens it
        if (row.GoesUpTo == Ceiling.NoAction)
        {
            return new Prepared(id, null, Refused("notActionable"));
        }
        var ceiling = CeilingOf(chosen);
        var entry = PlanEntryFactory.From(row, approver, time.GetUtcNow(), ceiling, SandboxBudget);
        if (entry is null)
        {
            return new Prepared(id, null, Refused("noRepository"));
        }
        // The staging of the test bed is declared: the first world is the staging. The lead proposes, the conductor checks the proposal against the powers as they are when it starts
        var brief = ProblemBrief.Of(row);
        var outcome = await lead.PlanAsync(new LeadInput(brief, ceiling, FirstWorld), cancellationToken);
        if (outcome.Plan is not { } plan)
        {
            return new Prepared(id, null, Refused($"leadUnavailable: {outcome.Failure}", entry.ProblemId));
        }
        if (LeadPlanChecks.Problems(plan, FirstWorld, ceiling) is { Count: > 0 } refused)
        {
            return new Prepared(id, null, Refused($"leadPlanRefused: {string.Join("; ", refused)}", entry.ProblemId));
        }
        return new Prepared(id, entry with { Brief = brief, Plan = plan }, null);
    }

    private static readonly World FirstWorld = World.Staging;

    private static async Task<ValidatedProblem> StartAsync(Prepared one, SandboxApprovals approvals, ProblemConductor conductor, MissionProgress progress, CancellationToken cancellationToken)
    {
        if (one.Refused is { } refused)
        {
            return refused;
        }
        var entry = one.Entry!;
        var id = one.RowId;
        if (!await approvals.PrepareAsync(entry, cancellationToken))
        {
            return new ValidatedProblem(id, entry.ProblemId, null, null, "noApproval");
        }
        // The staging of the test bed is declared, and its baseline is the snapshot of the lab VMs (a reset brings them back to it)
        var result = await conductor.StartAsync(entry, StagingDeclaration.DeclaredStaging, StagingRequirement.Dependencies, StagingState.BaselineProven, cancellationToken);
        if (result.Ok && result.Mission is { } mission && entry.Plan is { } plan)
        {
            // What the lead decided and why is shown with the first mission of the problem
            progress.Add(mission.Id, $"the lead planned: {plan.Summary} ({string.Join(" | ", plan.Reasons)})");
            if (plan.Complex)
            {
                progress.Add(mission.Id, $"the lead marked the problem complex: {plan.ComplexReason ?? "no reason given"}");
            }
        }
        return result.Ok
            ? new ValidatedProblem(id, entry.ProblemId, result.Mission?.Id, result.Mission?.World.ToString(), null)
            : new ValidatedProblem(id, entry.ProblemId, null, null, result.Refusal ?? "refused");
    }
}
