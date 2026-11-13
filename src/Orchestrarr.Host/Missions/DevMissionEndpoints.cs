using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Approvals;

namespace Orchestrarr.Host.Missions;

/// <summary>
/// Development only: drives a problem through the three worlds with **fake agents and fake facts**, so that the machinery (mandates, the controller's crossings, the loop, the stop) can be seen in
/// the sandbox before a real runner or agent exists. It is mapped only when the host runs in Development and holds no power: nothing leaves the stores.
/// </summary>
internal static class DevMissionEndpoints
{
    public static void MapDevMissionEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/dev/missions/demo", (string? scenario, string? application, bool? staging, MandateSigner signer, IMissionStore missions, IMandateLedger mandates, IProblemStore problems,
            IDossierStore dossiers, IRunnerDirectory runners, TimeProvider time, MissionsOptions options) =>
        {
            var facts = new DemoFacts();
            var conductor = new ProblemConductor(signer, missions, mandates, problems, dossiers, facts, runners, time, options.Conductor(), () => Guid.NewGuid().ToString("N"));
            var name = string.IsNullOrWhiteSpace(application) ? "demo-shop" : application.Trim();
            var problemId = $"demo-{Guid.NewGuid():N}"[..13];
            var entry = new PlanEntry(problemId, name, "deprecated-dependency", "Replace the call to the removed method", [$"example/{name}"], World.Release, new MissionBudget(20, 3, 50), new Validation("demo", null), time.GetUtcNow());
            var started = conductor.Start(entry, staging == false ? StagingDeclaration.DeclaredNoStaging : StagingDeclaration.DeclaredStaging, StagingRequirement.BlankMachine, StagingState.NotDefined, grant: null, rule: null);
            if (!started.Ok)
            {
                return Results.Json(new ErrorResponse(started.Refusal ?? "refused"), statusCode: StatusCodes.Status409Conflict);
            }
            var current = started.Mission!;
            var scenarioName = (scenario ?? "happy").ToLowerInvariant();
            while (true)
            {
                var mission = current;
                conductor.Started(mission.Id);
                if (scenarioName == "red" && mission.World == World.Release)
                {
                    facts.Ci = false;
                }
                conductor.HandOver(mission.Id, new Dossier(mission.Id, "done", $"worked in the {mission.World} world", ["sha256:demo"], [], mission.World == World.Staging, mission.World == World.Staging ? "a refactor" : null));
                var decision = scenarioName == "escalate" && mission.World == World.Git
                    ? new ControllerDecision(ControllerDecisionKind.Escalate, ["no fixed version exists"], null)
                    : new ControllerDecision(ControllerDecisionKind.Grant, ["the evidence supports the claim"], null);
                var result = conductor.Decide(mission.Id, decision);
                if (!result.Ok || result.Mandate is null)
                {
                    break;
                }
                current = result.Mission!;
            }
            return Results.Ok(missions.ForProblem(problemId).Select(mission => MissionEndpoints.View(mission, mandates, problems, dossiers)).ToList());
        }).RequireAuthorization(Permissions.ApprovalsCreate);
    }

    private sealed class DemoFacts : IFactsSource
    {
        public bool Ci { get; set; } = true;

        public PreconditionFacts Facts(string problemId, World world, Dossier? dossier) => new(
            world, true, true, false, StagingVerdict.Usable, dossier is not null, false, Ci, true, true, LoopVerdict.Allowed);
    }
}
