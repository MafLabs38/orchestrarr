using Orchestrarr.Core.Applications;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Infrastructure;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Missions;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Core.WorkItems;
using Orchestrarr.Host.Agents;
using Orchestrarr.Host.Approvals;
using Orchestrarr.Host.Decisions;
using Orchestrarr.Host.Live;
using Orchestrarr.Host.Tasks;

namespace Orchestrarr.Host.Missions;

/// <summary>The rows the maintainer ticked, with the commit each one was seen at: what was seen is what is validated (the table is computed again here).</summary>
public sealed record ValidatePlanRequest(List<ValidatedRow>? Rows);

public sealed record ValidatedRow(string? Id, string? HeadSha, bool StopBeforeProduction = false);

/// <summary>What became of one ticked row: the problem it started and its first mission, or why nothing started. <c>Queued</c>: it waits for its turn on its repository, where another problem is being done.</summary>
public sealed record ValidatedProblem(string RowId, string? ProblemId, string? MissionId, string? World, string? Refusal, bool Queued = false);

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

    // The places of an image that the lead is told about: a few are enough to know what the change touches
    private const int MaxPlaces = 10;

    public static void MapPlanEndpoints(this RouteGroupBuilder api)
    {
        // Whether the validation goes through the missions here: the screen sends the selection to the plan when it does, and says so
        api.MapGet("/plan", (IServiceProvider services) => Results.Ok(new PlanStatus(services.GetService<ProblemStarter>() is not null))).RequireAuthorization(Permissions.FleetRead);
        api.MapPost("/plan/validate", async (ValidatePlanRequest request, HttpContext context, IApproverAccessor accessor, WorkItemCatalog catalog, IApplicationCatalog applications, RunnerRegistry runners, IBatchQueue queue,
            IProblemHistory history, IApprovalStore approvalStore, TimeProvider time, RelevancePolicy policy, InfrastructureCatalog infrastructure, ProblemConductor conductor, IServiceProvider services,
            LeadPlanner lead, IDependencyFindings findings, IPipelineFailures pipelines, AgentsOptions agents, IMissionAudit audit, IStopSignal stop, ILiveNotifier live, CancellationToken ct) =>
        {
            var (approver, refusal) = ApprovalEndpoints.Authorize(accessor, context);
            if (approver is null)
            {
                return refusal!;
            }
            if (services.GetService<ProblemStarter>() is not { } starter)
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
            // The lead thinks about each problem first (two at a time), then the problems are started one after the other: nothing is started that the lead did not plan, and no approval is used for a problem that has no plan.
            // A row is asked once, at most Agents:LeadMaxRows rows are asked in one selection, and the global stop is read again before each question: every question is a run of the subscription
            // The rows are taken **in the order of the route** (ADR 0055: the runtime, then the alerts, then the updates), not in the order of the ticks, and a problem **starts as soon as its own plan is ready**: the machines
            // do not wait for the lead to have thought about every row of the selection (a plan is a minute of the subscription, and a hundred rows would keep them idle for an hour). The planning goes on behind it
            var rows = InRouteOrder(request.Rows ?? [], table);
            // Each row is a decision taken from now on: the table does not offer it again and the Flow shows it at the entry of the line while the lead plans it (ADR 0056)
            var planned = services.GetService<ProblemPlanning>();
            var problemIds = rows.Select(chosen => chosen.Id is { } rowId ? PlanEntryFactory.ProblemIdOf(rowId) : null).ToList();
            foreach (var (chosen, index) in rows.Select((chosen, index) => (chosen, index)))
            {
                if (problemIds[index] is { } id && chosen.Id is { } rowId && table.TryGetValue(rowId, out var known))
                {
                    planned?.Add(new PlannedRow(id, PlannedTitle(known), known.Kind.ToString(), known.Source ?? known.Application, time.GetUtcNow()));
                }
            }
            live.Publish(LiveTopics.Missions);
            var planning = rows.Select((chosen, index) => PrepareAsync(chosen, table, approver.Subject, lead, new ProblemSources(findings, pipelines), conductor, audit, stop, NotAsked(index, agents.LeadMaxRows), time, ct)).ToList();
            // One work site at a time on a repository: the first problem of a free repository starts, the others wait for their turn (ProblemQueue). The plans of a lot are placed in the order of its route, and the lots
            // go side by side: a plan that is slow to come holds its own lot only, never the machines of the others
            var placed = new ValidatedProblem?[planning.Count];
            var lots = planning.Select((plan, index) => (Plan: plan, Index: index, Lot: LotOf(rows[index], table)))
                .GroupBy(item => item.Lot, StringComparer.Ordinal)
                .Select(async lot =>
                {
                    foreach (var (plan, index, _) in lot)
                    {
                        try
                        {
                            placed[index] = await starter.StartOrQueueAsync(await plan, ct);
                        }
                        finally
                        {
                            if (problemIds[index] is { } id)
                            {
                                planned?.Remove(id);
                            }
                            live.Publish(LiveTopics.Missions);
                        }
                    }
                });
            try
            {
                await Task.WhenAll(lots);
            }
            finally
            {
                // A validation that ends early (the request was cancelled, a plan failed) leaves nothing shown as being planned
                foreach (var id in problemIds.OfType<string>())
                {
                    planned?.Remove(id);
                }
                live.Publish(LiveTopics.Missions);
            }
            var started = placed.OfType<ValidatedProblem>().ToList();
            live.Publish(LiveTopics.Missions);
            return Results.Json(new ValidatePlanResponse(started), statusCode: StatusCodes.Status201Created);
        }).RequireAuthorization(Permissions.ApprovalsCreate).RequireRateLimiting(ApprovalsServiceCollectionExtensions.ApprovingPolicy);
    }

    // What the lead is told of the places of an image: the ones in the repository of the problem that no automation rewrites (the same places as the gate of the table), plain lines, a few of them
    internal static IReadOnlyList<string> ImagePlaces(IReadOnlyList<Orchestrarr.Core.Infrastructure.CodeLocation> places, string repository) =>
        [.. places.Where(place => string.Equals(place.Repository, repository, StringComparison.OrdinalIgnoreCase) && place.Managed is null).Take(MaxPlaces).Select(place => $"image written in {place.Path} line {place.Line} as {place.Image.Name}{(place.Image.Tag is { } tag ? $":{tag}" : string.Empty)}")];

    // The rows of a selection, each once, in the order of the route (ADR 0055); a row that the table does not know goes last. The order is stable: rows of the same rank keep the order of the ticks
    internal static List<ValidatedRow> InRouteOrder(IEnumerable<ValidatedRow> chosen, IReadOnlyDictionary<string, ProblemRow> table) =>
        [.. chosen.DistinctBy(row => row.Id, StringComparer.Ordinal).OrderBy(row => row.Id is { } rowId && table.TryGetValue(rowId, out var known) ? RouteRank.Of(known.Kind, known.Nature) : int.MaxValue)];

    internal static World CeilingOf(ValidatedRow chosen) => chosen.StopBeforeProduction ? World.Git : PlanCeiling;

    // What a person reads of a row being planned: the same name as the flow gives it once it is placed
    private static string PlannedTitle(ProblemRow row) => FlowView.TitleOf(row.Kind.ToString(), row.Description, row.ParsedChange ?? row.Description ?? string.Empty);

    // The lot of a row is its repository (ADR 0055); a row that the table does not know is a lot of its own
    private static string LotOf(ValidatedRow chosen, Dictionary<string, ProblemRow> table) =>
        chosen.Id is { } rowId && table.TryGetValue(rowId, out var known) ? known.Source ?? known.Application : $"row:{chosen.Id}";

    // Why a row is not asked about at all: the selection is beyond what the lead is asked about at once (Agents:LeadMaxRows)
    private static string? NotAsked(int index, int maxRows) => index >= maxRows ? "tooManyToPlanAtOnce" : null;



    private static async Task<PreparedProblem> PrepareAsync(ValidatedRow chosen, Dictionary<string, ProblemRow> table, string approver, LeadPlanner lead, ProblemSources sources, ProblemConductor conductor, IMissionAudit audit, IStopSignal stop, string? notAsked, TimeProvider time, CancellationToken cancellationToken)
    {
        var id = chosen.Id ?? string.Empty;
        PreparedProblem Refused(string reason, string? problemId = null) => new(id, null, new ValidatedProblem(id, problemId, null, null, reason));
        if (!table.TryGetValue(id, out var row))
        {
            return Refused("theTableChanged");
        }
        if (row.HeadSha is not null && !string.Equals(row.HeadSha, chosen.HeadSha, StringComparison.Ordinal))
        {
            return Refused("thePullRequestMoved");
        }
        // A row that nothing can act on is not started, and a stop before production that the operator ticked is carried into the plan: it narrows what the row asks for, never widens it
        if (row.GoesUpTo == Ceiling.NoAction)
        {
            return Refused("notActionable");
        }
        var ceiling = CeilingOf(chosen);
        var entry = PlanEntryFactory.From(row, approver, time.GetUtcNow(), ceiling, SandboxBudget);
        if (entry is null)
        {
            return Refused("noRepository");
        }
        if (notAsked is not null)
        {
            return Refused(notAsked, entry.ProblemId);
        }
        // The staging of the test bed is declared: the first world is the staging. The lead proposes; the conductor checks the proposal against the powers as they are when the problem starts
        var brief = ProblemBrief.Of(row);
        // A dependency alert is about dependencies: what the source knows of them goes to the lead, as data, with the problem
        if (row.Kind == ProblemKind.DependencyAlert && entry.Repositories.Count == 1)
        {
            var known = await sources.Findings.ReadAsync(entry.Repositories[0], cancellationToken);
            brief = brief with { Details = [.. known.Select(finding => finding.Line)] };
        }
        // An image problem is about the places where the image is written: what the control plane read of them goes to the lead, as data, with the problem (the file, the line, the reference as written)
        if ((row.Kind is ProblemKind.ImageUpdate or ProblemKind.FloatingImage) && row.Locations is { Count: > 0 } places)
        {
            brief = brief with { Details = ImagePlaces(places, entry.Repositories[0]) };
        }
        // A red pipeline is about the pipelines that are red: which ones, read on the forge, go to the lead as data, so that the worker repairs those and not the one it guesses (ADR 0055)
        if (row.Kind == ProblemKind.FailingCi && entry.Repositories.Count == 1)
        {
            var red = await sources.Pipelines.ReadAsync(entry.Repositories[0], cancellationToken);
            brief = brief with { Details = red.Count > 0 ? red : ["the forge says the pipeline of the default branch fails, and which workflow could not be read: run them all and repair the ones that fail"] };
        }
        // The global stop is read again for each question: a row that waits for a free slot may be asked long after the request came
        if (stop.Engaged)
        {
            return Refused("stopEngaged", entry.ProblemId);
        }
        // The lead is told how far the problem can really go (what was asked and the grant of the application), so that it plans within what the conductor accepts
        if (conductor.PowersFor(entry) is not { } reachable || reachable < FirstWorld)
        {
            return Refused("noPower", entry.ProblemId);
        }
        LeadOutcome outcome;
        try
        {
            outcome = await lead.PlanAsync(new LeadInput(brief, reachable, FirstWorld), cancellationToken, () => stop.Engaged);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // One row whose lead cannot answer does not stop the others
            outcome = new LeadOutcome(null, failure.GetType().Name);
        }
        if (outcome.Plan is not { } plan)
        {
            _ = await audit.RecordAsync(MissionAuditEvents.LeadRefused, entry.ProblemId, $"no plan: {outcome.Failure}", CancellationToken.None);
            return Refused("leadUnavailable", entry.ProblemId);
        }
        // A plan that cannot start is refused here, before an approval is made for it; the conductor checks it again, on the powers as they are, when the problem starts
        if (LeadPlanChecks.Problems(plan, FirstWorld, reachable) is { Count: > 0 } refused)
        {
            _ = await audit.RecordAsync(MissionAuditEvents.LeadRefused, entry.ProblemId, $"plan refused: {string.Join("; ", refused)}", CancellationToken.None);
            return Refused("leadPlanRefused", entry.ProblemId);
        }
        return new PreparedProblem(id, entry with { Brief = brief, Plan = plan }, null);
    }

    private static readonly World FirstWorld = World.Staging;
}

/// <summary>What the control plane reads about a problem before the lead plans it: the findings about its dependencies, and the pipelines that are red on its default branch.</summary>
internal sealed record ProblemSources(IDependencyFindings Findings, IPipelineFailures Pipelines);
