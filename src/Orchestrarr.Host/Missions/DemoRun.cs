using Orchestrarr.Core.Accounts;
using Orchestrarr.Core.Missions;
using Orchestrarr.Core.SecondFactor;
using Orchestrarr.Host.Approvals;

namespace Orchestrarr.Host.Missions;

/// <summary>What the demo plays: the straight path, a release whose pipelines turn red, or a controller that escalates at the Git world.</summary>
internal enum DemoScenario
{
    Happy,
    Red,
    Escalate,
}

/// <summary>
/// Development only: drives a problem through the three worlds with **fake agents and fake facts**, so that the machinery (mandates, the controller's crossings, the loop, the stop) can be seen in the sandbox
/// before a real runner or agent exists. It holds no power: nothing leaves the stores.
/// </summary>
internal sealed class DemoRun(MissionStores stores, ProblemLocks locks, IRunnerDirectory runners, IMissionAudit audit, TimeProvider time, MissionsOptions options)
{
    /// <summary>Nothing given is the straight path; a name that is not a scenario is refused, never played as the straight path.</summary>
    public static bool IsScenario(string? text, out DemoScenario scenario)
    {
        scenario = DemoScenario.Happy;
        return string.IsNullOrWhiteSpace(text) || (Enum.TryParse(text.Trim(), ignoreCase: true, out scenario) && Enum.IsDefined(scenario));
    }

    public async Task<IResult> RunAsync(DemoScenario scenario, string? application, bool staging, CancellationToken cancellationToken)
    {
        var facts = new DemoFacts();
        // A key of its own, made for this call: a demo mandate is never signed with the key of the control plane, so no real runner accepts it
        using var demoKey = MandateSigner.Create();
        // A real approval authority with a real second factor of its own (a demo secret and the code of the moment), so that the certificates the demo mandates fall inside are real ones
        using var authority = DemoApprovals.NewAuthority(time, out var approvals);
        using var verifier = new ApprovalVerifier(authority.PublicKey(), time);
        var conductor = new ProblemConductor(demoKey, stores, new PowerSources(approvals, verifier, new DemoWithdrawals(), new DemoGrants(), new DemoStop()), new ConductorServices(facts, runners, audit, time, () => Guid.NewGuid().ToString("N")), options.Conductor(), locks);
        var name = string.IsNullOrWhiteSpace(application) ? "demo-shop" : application.Trim();
        var problemId = $"demo-{Guid.NewGuid():N}"[..13];
        var entry = new PlanEntry(problemId, name, "deprecated-dependency", "Replace the call to the removed method", [$"example/{name}"], World.Release, new MissionBudget(20, 3, 50), new Validation("demo", null), time.GetUtcNow());
        // The approval is made before the conductor asks for it: the conductor reads it under its lock and never waits for the authority there
        await approvals.PrepareAsync(entry, cancellationToken);
        var started = await conductor.StartAsync(entry, staging ? StagingDeclaration.DeclaredStaging : StagingDeclaration.DeclaredNoStaging, StagingRequirement.BlankMachine, StagingState.NotDefined, cancellationToken);
        var current = started.Ok ? started.Mission : null;
        // The loop of the demo ends by the limit of the loop of a problem (the conductor refuses the mandate that goes past it); the count here only guarantees that the demo ends
        for (var round = 0; current is not null && round <= options.MaxMandatesPerProblem + 2; round++)
        {
            _ = await conductor.StartedAsync(current.Id, cancellationToken);
            // In the red scenario the pipelines are red only after the release, so that every return to the first world goes through the worlds again
            facts.Ci = !(scenario == DemoScenario.Red && current.World == World.Release);
            _ = await conductor.HandOverAsync(current.Id, new Dossier(current.Id, "done", $"worked in the {current.World} world", ["sha256:demo"], [], current.World == World.Staging, current.World == World.Staging ? "a refactor" : null, Commit: "demo-commit"), cancellationToken);
            var decision = scenario == DemoScenario.Escalate && current.World == World.Git
                ? new ControllerDecision(ControllerDecisionKind.Escalate, ["no fixed version exists"], null)
                : new ControllerDecision(ControllerDecisionKind.Grant, ["the evidence supports the claim"], null);
            var result = await conductor.DecideAsync(current.Id, decision, cancellationToken);
            current = result.Mandate is not null ? result.Mission : null;
        }
        if (!started.Ok)
        {
            return Results.Json(new ErrorResponse(started.Refusal ?? "refused"), statusCode: StatusCodes.Status409Conflict);
        }
        return Results.Ok(stores.Missions.ForProblem(problemId).Select(mission => MissionEndpoints.View(mission, stores.Mandates, stores.Problems, stores.Dossiers)).ToList());
    }

    // The demo has its own authority, its own second factor and a grant for every application: for the sandbox and the tests, never for a deployment
    private sealed class DemoApprovals(ApprovalAuthority authority, ISecondFactorSecrets secrets, TimeProvider time, SecondFactorOptions options) : IApprovalSource
    {
        private readonly Dictionary<string, SignedApproval> _prepared = new(StringComparer.Ordinal);

        public static ApprovalAuthority NewAuthority(TimeProvider time, out DemoApprovals approvals)
        {
            var secret = OtpAuthUri.NewSecret();
            var factor = new SecondFactorOptions();
            var secrets = new DemoSecrets(secret);
            var authority = new ApprovalAuthority(new SecondFactorGuard(secrets, new InMemorySecondFactorUseLedger(), time, factor), time, () => Guid.NewGuid().ToString("N"));
            approvals = new DemoApprovals(authority, secrets, time, factor);
            return authority;
        }

        public async Task PrepareAsync(PlanEntry entry, CancellationToken cancellationToken)
        {
            var secret = await secrets.FindAsync("demo", cancellationToken);
            if (secret is null)
            {
                return;
            }
            var code = Totp.Code(secret, Totp.StepAt(time.GetUtcNow(), options.Totp), options.Totp);
            var request = new ApprovalRequest(CertificateKind.Problem, entry.ProblemId, entry.ApplicationId, entry.Repositories, entry.MaxWorld, entry.Budget, TimeSpan.FromHours(2), PipelineChanges.ForbiddenFor(entry));
            // The demo plays both sides: it proposes, then confirms what it proposed with the code of the demo account
            if (authority.Propose(request, "demo") is { } proposal && (await authority.ApproveAsync(proposal.Id, "demo", code, cancellationToken)).Approval is { } approval)
            {
                _prepared[entry.ProblemId] = approval;
            }
        }

        public SignedApproval? For(PlanEntry entry) => _prepared.GetValueOrDefault(entry.ProblemId);
    }

    private sealed class DemoSecrets(byte[] secret) : ISecondFactorSecrets
    {
        public ValueTask<byte[]?> FindAsync(string subject, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(subject == "demo" ? secret : null);
    }

    private sealed class DemoGrants : IGrantSource
    {
        public World? MaxWorldFor(string applicationId) => World.Release;
    }

    private sealed class DemoWithdrawals : IApprovalWithdrawals
    {
        public bool IsWithdrawn(string certificateId) => false;
    }

    private sealed class DemoStop : IStopSignal
    {
        public bool Engaged => false;
    }

    private sealed class DemoFacts : IFactsSource
    {
        public bool Ci { get; set; } = true;

        public ValueTask<ObservedFacts> FactsAsync(FactsQuery query, CancellationToken cancellationToken) => ValueTask.FromResult(new ObservedFacts(BudgetAvailable: true, DiffIsClean: true, CiGreenOnExactCommit: Ci, ArtifactIsTheOneProvedInStaging: true, WayBackKnown: true, WorkerClaimMatchesFacts: true, MergeMayDeploy: false, ExecutedFilesTouched: false, TagCreationBlocked: true, BranchCannotReachWriteCredentials: true, HeadCommit: query.Commit, IdentityConfinedToItsBranches: true, ChangedPaths: ["src/Orders.cs", "tests/OrdersTests.cs"]));
    }
}
