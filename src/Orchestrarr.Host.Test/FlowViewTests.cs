using Orchestrarr.Core.Machines;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

public sealed class FlowViewTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);

    private static PlanEntry Entry(string problem, string repository = "o/shop", string description = "nginx:1.14 → 1.28", World max = World.Release) =>
        new(problem, "shop", "ImageUpdate", "o", [repository], max, new MissionBudget(1, 1, 1), new Validation("x", null), T0, Brief: new ProblemBrief("ImageUpdate", repository, description, false));

    private static ProblemRun Run(string problem, ProblemState state = ProblemState.InProgress, string repository = "o/shop", int priority = FlowPriority.Normal, int startedMinutes = 0, string description = "nginx:1.14 → 1.28") =>
        new(problem, "shop", state, T0.AddMinutes(startedMinutes), Entry(problem, repository, description), World.Release, true, new SignedApproval(new ApprovalCertificate("c", CertificateKind.Problem, problem, "shop", [repository], World.Release, new MissionBudget(1, 1, 1), T0, T0.AddHours(1), "x", "n", [], 8), []), Priority: priority);

    private static Mission Mission(string id, string problem, World world, MissionState state, MissionEnd? end = null) =>
        new(id, problem, "shop", world, "o", new MissionFrame(["o/shop"], [], ""), new MissionBudget(1, 1, 1), state, MandateId: "mandate-" + id, End: end);

    private static IReadOnlyList<FlowProblem> Build(IReadOnlyList<Mission> missions, IProblemStore problems, IReadOnlyList<QueuedProblem>? waiting = null, IReadOnlyList<Machine>? machines = null, bool runner = true,
        Func<string, (string?, DateTimeOffset?)>? news = null, Func<Mission, DateTimeOffset?>? issued = null, Func<Mission, bool>? reworkPending = null) =>
        FlowView.Build(missions, problems, waiting ?? [], machines ?? [], runner, news ?? (_ => (null, null)), issued ?? (_ => T0), reworkPending);

    private static InMemoryProblemStore Store(params ProblemRun[] runs)
    {
        var store = new InMemoryProblemStore();
        foreach (var run in runs)
        {
            store.Save(run);
        }
        return store;
    }

    [Fact]
    public void A_problem_shows_each_world_where_it_stands_and_what_its_runner_said_last()
    {
        var flow = Build([Mission("m2", "p1", World.Git, MissionState.Running), Mission("m1", "p1", World.Staging, MissionState.Accepted, MissionEnd.Done)], Store(Run("p1")),
            news: id => id == "m2" ? ("12:00:01 Merged", T0.AddMinutes(5)) : (null, null));

        var problem = flow.Should().ContainSingle().Subject;
        problem.Title.Should().Be("nginx:1.14 → 1.28");
        problem.State.Should().Be("inProgress");
        problem.Steps.Select(step => $"{step.World}:{step.State}").Should().Equal("Staging:done", "Git:running", "Release:notStarted");
        problem.Activity.Should().Be(new FlowActivity("running", "12:00:01 Merged", T0.AddMinutes(5), null));
    }

    [Fact]
    public void The_controller_reading_a_dossier_is_said_so()
    {
        Build([Mission("m1", "p1", World.Staging, MissionState.HandingOver)], Store(Run("p1"))).Single().Activity!.Kind.Should().Be("controller");
    }

    [Fact]
    public void A_problem_that_waits_for_its_turn_names_the_problem_that_holds_the_repository()
    {
        var waiting = new QueuedProblem("row-2", Entry("p2", description: "lodash 4.17.20 → 4.17.21"), T0.AddMinutes(1));

        var flow = Build([Mission("m1", "p1", World.Staging, MissionState.Running)], Store(Run("p1")), [waiting]);

        var queued = flow.Single(problem => problem.ProblemId == "p2");
        queued.State.Should().Be("queued");
        queued.Activity!.Waiting.Should().Be(new FlowWaiting("turn", "nginx:1.14 → 1.28"));
        flow.Select(problem => problem.ProblemId).Should().Equal("p1", "p2");
    }

    [Theory]
    [InlineData(MachineState.InProcess, true, "machine")]
    [InlineData(MachineState.Idle, true, "pickup")]
    [InlineData(MachineState.Idle, false, "runner")]
    public void A_mission_that_no_runner_has_taken_says_what_is_in_the_way(MachineState machineState, bool runner, string code)
    {
        var machines = new[] { new Machine("leipzig", MachineRole.Staging, machineState, T0) };

        var waiting = Build([Mission("m1", "p1", World.Staging, MissionState.Mandated)], Store(Run("p1")), machines: machines, runner: runner).Single().Activity!.Waiting!;

        waiting.Code.Should().Be(code);
        (code == "machine" ? waiting.Subject : "leipzig (InProcess)").Should().Contain("leipzig");
    }

    [Fact]
    public void A_problem_that_waits_behind_another_of_its_repository_is_told_before_the_machine_is()
    {
        var run = Store(Run("p1", startedMinutes: 0), Run("p2", startedMinutes: 5, description: "lodash"));

        var waiting = Build([Mission("m2", "p2", World.Staging, MissionState.Mandated)], run, machines: [new Machine("leipzig", MachineRole.Staging, MachineState.InProcess, T0)], issued: _ => T0.AddMinutes(10))
            .Single(problem => problem.ProblemId == "p2").Activity!.Waiting!;

        waiting.Should().Be(new FlowWaiting("turn", "nginx:1.14 → 1.28"));
    }

    [Fact]
    public void Only_the_missions_of_the_run_count_when_the_same_row_was_done_before()
    {
        var old = Mission("old", "p1", World.Staging, MissionState.Ended, MissionEnd.Failed);
        var current = Mission("new", "p1", World.Staging, MissionState.Running);

        var flow = Build([current, old], Store(Run("p1", startedMinutes: 30)), issued: mission => mission.Id == "old" ? T0 : T0.AddMinutes(31));

        flow.Single().Steps[0].Should().Be(new FlowStep("Staging", "running", "new", 1));
    }

    [Fact]
    public void A_release_that_failed_shows_its_priority_and_the_world_done_again()
    {
        var flow = Build([Mission("m4", "p1", World.Staging, MissionState.Running), Mission("m3", "p1", World.Release, MissionState.Ended, MissionEnd.Refused), Mission("m2", "p1", World.Git, MissionState.Accepted, MissionEnd.Done),
            Mission("m1", "p1", World.Staging, MissionState.Accepted, MissionEnd.Done)], Store(Run("p1", priority: FlowPriority.FirstUrgent)));

        var problem = flow.Single();
        problem.Priority.Should().Be(FlowPriority.FirstUrgent);
        problem.Steps.Select(step => $"{step.World}:{step.State}:{step.Attempts}").Should().Equal("Staging:running:2", "Git:done:1", "Release:ended:1");
    }

    [Fact]
    public void What_is_over_comes_after_what_is_going_on_and_is_kept_to_a_few()
    {
        var finished = Enumerable.Range(1, 30).Select(index => Run($"f{index}", ProblemState.Closed, repository: $"o/r{index}")).ToArray();
        var missions = finished.Select(run => Mission("m-" + run.ProblemId, run.ProblemId, World.Staging, MissionState.Accepted, MissionEnd.Done)).Append(Mission("m-live", "live", World.Staging, MissionState.Running)).ToList();

        var flow = Build(missions, Store([.. finished, Run("live", repository: "o/live")]));

        flow[0].ProblemId.Should().Be("live");
        flow.Should().HaveCount(21);
        flow.Skip(1).Should().OnlyContain(problem => problem.State == "closed" && problem.Activity == null);
    }

    [Fact]
    public void A_mission_that_was_sent_back_waits_until_a_runner_is_offered_it_again_and_names_the_mission_the_runner_is_busy_with()
    {
        var store = Store(Run("p1", repository: "o/a", startedMinutes: 0), Run("p2", repository: "o/b", startedMinutes: 1, description: "lodash"));
        var sentBack = Mission("m1", "p1", World.Git, MissionState.Running) with { SendBacks = 1 };
        var busy = Mission("m2", "p2", World.Staging, MissionState.Running);

        var flow = Build([busy, sentBack], store, reworkPending: mission => mission.Id == "m1", issued: _ => T0.AddMinutes(10));

        var first = flow.Single(problem => problem.ProblemId == "p1");
        first.Steps.Single(step => step.World == "Git").State.Should().Be("waiting");
        first.Activity!.Waiting.Should().Be(new FlowWaiting("busy", "lodash"));
        flow.Single(problem => problem.ProblemId == "p2").Activity!.Kind.Should().Be("running");
    }

    [Fact]
    public void The_order_of_what_is_going_on_is_the_order_it_was_started_in_whatever_moved_last()
    {
        var store = Store(Run("p1", repository: "o/a", startedMinutes: 0), Run("p2", repository: "o/b", startedMinutes: 1));

        Build([Mission("m2", "p2", World.Staging, MissionState.Running), Mission("m1", "p1", World.Staging, MissionState.Running)], store).Select(problem => problem.ProblemId).Should().Equal("p1", "p2");
        Build([Mission("m1", "p1", World.Staging, MissionState.Running), Mission("m2", "p2", World.Staging, MissionState.Running)], store).Select(problem => problem.ProblemId).Should().Equal("p1", "p2");
    }

    [Fact]
    public void A_problem_that_has_just_left_the_line_is_shown_once()
    {
        var waiting = new QueuedProblem("row", Entry("p1"), T0);

        Build([Mission("m1", "p1", World.Staging, MissionState.Mandated)], Store(Run("p1")), [waiting]).Should().ContainSingle().Which.State.Should().Be("inProgress");
    }

    [Fact]
    public void The_worlds_shown_are_the_ones_the_lead_planned()
    {
        var plan = new LeadPlan([new PlannedStep(World.Staging, "a"), new PlannedStep(World.Git, "b")], false, null, []);
        var run = Run("p1") with { Entry = Entry("p1") with { Plan = plan } };

        Build([Mission("m1", "p1", World.Staging, MissionState.Running)], Store(run)).Single().Steps.Select(step => step.World).Should().Equal("Staging", "Git");
    }
}
