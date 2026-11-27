using Orchestrarr.Core.Machines;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

public sealed class FlowBoardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    private static FlowProblem Problem(string id, string repository, string state, FlowActivity? activity = null, string? kind = "DependencyUpdate", int minutes = 0, string? note = null, IReadOnlyList<FlowStep>? steps = null) =>
        new(id, $"title of {id}", kind, repository, state, FlowPriority.Normal, T0.AddMinutes(minutes), steps ?? [new FlowStep("Staging", "notStarted", null, 0), new FlowStep("Git", "notStarted", null, 0)], activity, note);

    private static Mission Mission(string id, string problemId, World world) =>
        new(id, problemId, "app", world, "objective", new MissionFrame(["o/one"], [], "here"), new MissionBudget(30, 3, 50), MissionState.Running);

    private static FlowBoardView Board(IReadOnlyList<FlowProblem> flow, IReadOnlyList<Machine>? machines = null, IReadOnlyList<Mission>? missions = null, int gitLanes = 3) =>
        FlowBoard.Build(flow, machines ?? [], missions ?? [], world => world == "Git" ? gitLanes : 0);

    [Fact]
    public void Each_operation_is_placed_in_the_workshop_where_it_is_and_says_what_it_does_there()
    {
        var flow = new[]
        {
            Problem("p-work", "o/one", "inProgress", new FlowActivity("running", "The free agent works", T0, null), steps: [new FlowStep("Staging", "running", "m-1", 1), new FlowStep("Git", "notStarted", null, 0)]),
            Problem("p-merge", "o/two", "inProgress", new FlowActivity("waiting", null, T0, new FlowWaiting("machine", "leipzig (InProcess)")), steps: [new FlowStep("Staging", "done", "m-0", 1), new FlowStep("Git", "waiting", "m-2", 1)]),
            Problem("p-line", "o/one", "queued", new FlowActivity("waiting", null, T0, new FlowWaiting("turn", "title of p-work"))),
            Problem("p-bot", "o/one", "queued", new FlowActivity("waiting", null, T0, new FlowWaiting("bot", "it is behind its base branch"))),
            Problem("p-done", "o/two", "closed"),
            Problem("p-grain", "o/two", "escalated", note: "no fixed version exists"),
        };

        var board = Board(flow);

        board.Places.Should().Equal("queue", "Staging", "Git", "out");
        var all = board.Lots.SelectMany(lot => lot.Operations).ToDictionary(operation => operation.ProblemId);
        all["p-work"].Should().Match<BoardOperation>(operation => operation.Place == "Staging" && operation.State == "working" && operation.Detail == "The free agent works");
        all["p-merge"].Should().Match<BoardOperation>(operation => operation.Place == "Git" && operation.State == "waitingMachine");
        all["p-line"].Should().Match<BoardOperation>(operation => operation.Place == "queue" && operation.State == "waitingTurn");
        all["p-bot"].State.Should().Be("waitingBot", "an operation parked for its bot says so, not only that it waits");
        all["p-done"].Should().Match<BoardOperation>(operation => operation.Place == "out" && operation.State == "done");
        all["p-grain"].Should().Match<BoardOperation>(operation => operation.Place == "out" && operation.State == "escalated" && operation.Detail == "no fixed version exists", "an escalated operation waits for a person with its reason: it is not put aside");
        board.Counters.Should().Be(new BoardCounters(Queued: 2, InProcess: 1, Waiting: 1, Done: 1, PutAside: 0, OldestQueuedSince: T0, Escalated: 1));
    }

    [Fact]
    public void A_row_ticked_again_is_shown_once_as_its_new_run()
    {
        var flow = new[]
        {
            Problem("p-again", "o/one", "queued", new FlowActivity("waiting", null, T0, new FlowWaiting("turn", null))),
            Problem("p-again", "o/one", "closed"),
        };

        var board = Board(flow);

        board.Lots.Single().Operations.Should().ContainSingle().Which.Place.Should().Be("queue");
        board.Counters.Done.Should().Be(0);
    }

    [Fact]
    public void A_lot_shows_what_is_worked_on_first_then_its_line_in_the_order_it_goes_then_what_is_over()
    {
        // The flow lists the line in the order it goes (priority, route, arrival: ProblemQueue.InOrder); here a person raised the bump above the alert, which arrived first
        var flow = new[]
        {
            Problem("p-bump", "o/one", "queued", new FlowActivity("waiting", null, T0, new FlowWaiting("turn", null)), "DependencyUpdate", minutes: 1),
            Problem("p-done", "o/one", "closed", minutes: 0),
            Problem("p-alert", "o/one", "queued", new FlowActivity("waiting", null, T0, new FlowWaiting("turn", null)), "DependencyAlert", minutes: 2),
            Problem("p-image", "o/one", "inProgress", new FlowActivity("running", null, T0, null), "ImageUpdate", steps: [new FlowStep("Staging", "running", "m-1", 1)]),
        };

        var lot = Board(flow).Lots.Single();

        lot.Repository.Should().Be("o/one");
        lot.Operations.Select(operation => operation.ProblemId).Should().Equal("p-image", "p-bump", "p-alert", "p-done");
    }

    [Fact]
    public void A_staging_workshop_shows_its_machines_with_their_state_and_the_operation_each_works_on()
    {
        var machines = new[]
        {
            new Machine("leipzig", MachineRole.Staging, MachineState.InProcess, T0, "m-1"),
            new Machine("dresden", MachineRole.Staging, MachineState.Down, T0, Reason: "the qualification failed: disk"),
            new Machine("berlin", MachineRole.Staging, MachineState.Finished, T0),
            new Machine("bilbao", MachineRole.Release, MachineState.Idle, T0),
        };
        var flow = new[] { Problem("p-work", "o/one", "inProgress", new FlowActivity("running", null, T0, null), steps: [new FlowStep("Staging", "running", "m-1", 1)]) };

        var board = Board(flow, machines, [Mission("m-1", "p-work", World.Staging)]);

        board.Places.Should().Equal("queue", "Staging", "Git", "Release", "out");
        var staging = board.Workshops.Single(workshop => workshop.World == "Staging");
        staging.Equipment.Select(equipment => (equipment.Id, equipment.State)).Should().Equal(("berlin", "qualifying"), ("dresden", "down"), ("leipzig", "inProcess"));
        staging.Equipment.Single(equipment => equipment.Id == "leipzig").Should().Match<BoardEquipment>(equipment => equipment.ProblemId == "p-work" && equipment.Title == "title of p-work");
        staging.Equipment.Single(equipment => equipment.Id == "dresden").Reason.Should().Contain("disk", "a machine that is down says why");
        board.Workshops.Single(workshop => workshop.World == "Release").Equipment.Single().State.Should().Be("idle");
        board.Lots.Single().Operations.Single().Equipment.Should().Be("leipzig");
    }

    [Fact]
    public void The_forge_shows_its_slots_filled_by_the_operations_it_works_on_and_the_buffer_in_front_of_it()
    {
        var flow = new[]
        {
            Problem("p-a", "o/one", "inProgress", new FlowActivity("running", null, T0, null), steps: [new FlowStep("Git", "running", "m-a", 1)]),
            Problem("p-b", "o/two", "inProgress", new FlowActivity("controller", null, T0, null), steps: [new FlowStep("Git", "reading", "m-b", 1)]),
            Problem("p-c", "o/three", "inProgress", new FlowActivity("waiting", null, T0, new FlowWaiting("busy", "Git 3/3")), steps: [new FlowStep("Git", "waiting", "m-c", 1)]),
        };

        var git = Board(flow, gitLanes: 3).Workshops.Single(workshop => workshop.World == "Git");

        git.Equipment.Select(slot => slot.State).Should().Equal("inProcess", "inProcess", "idle");
        git.Equipment.Where(slot => slot.ProblemId is not null).Select(slot => slot.ProblemId).Should().Equal("p-a", "p-b");
        git.InProcess.Should().Be(2);
        git.Waiting.Should().Be(1);
        git.Why.Should().Equal("waitingLane");
        Board(flow, gitLanes: 0).Workshops.Single(workshop => workshop.World == "Git").Equipment.Should().HaveCount(2, "a runner that did not say its lanes shows one slot per operation at work");
    }
}
