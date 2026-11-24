using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

public sealed class MissionOrderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);

    private static Mission MissionOf(string id, string problem, World world) =>
        new(id, problem, "app", world, "o", new MissionFrame(["o/r"], [], ""), new MissionBudget(1, 1, 1), MissionState.Mandated);

    private static void Add(InMemoryProblemStore store, string problem, int priority, int startedMinutes = 0) =>
        store.Save(new ProblemRun(problem, "app", ProblemState.InProgress, T0.AddMinutes(startedMinutes), new PlanEntry(problem, "app", "c", "o", ["o/r"], World.Release, new MissionBudget(1, 1, 1), new Validation("x", null), T0),
            World.Release, true, new SignedApproval(new ApprovalCertificate("c", CertificateKind.Problem, problem, "app", ["o/r"], World.Release, new MissionBudget(1, 1, 1), T0, T0.AddHours(1), "x", "n", [], 8), []), Priority: priority));

    [Fact]
    public void The_more_urgent_problem_is_offered_before_a_later_world_of_a_normal_one()
    {
        var problems = new InMemoryProblemStore();
        Add(problems, "normal", FlowPriority.Normal);
        Add(problems, "hot", FlowPriority.FirstUrgent);

        new[] { MissionOf("m1", "normal", World.Release), MissionOf("m2", "hot", World.Staging) }.FirstNext(problems)!.Id.Should().Be("m2");
    }

    [Fact]
    public void A_mission_whose_problem_is_unknown_goes_last()
    {
        var problems = new InMemoryProblemStore();
        Add(problems, "known", FlowPriority.Normal);

        new[] { MissionOf("m1", "unknown", World.Release), MissionOf("m2", "known", World.Staging) }.FirstNext(problems)!.Id.Should().Be("m2");
    }

    [Fact]
    public void At_the_same_priority_the_later_world_goes_first_then_the_identifier()
    {
        var problems = new InMemoryProblemStore();
        Add(problems, "a", FlowPriority.Normal);
        Add(problems, "b", FlowPriority.Normal);

        new[] { MissionOf("m2", "a", World.Staging), MissionOf("m3", "b", World.Git), MissionOf("m1", "b", World.Git) }.FirstNext(problems)!.Id.Should().Be("m1");
    }

    [Fact]
    public void A_raised_priority_does_not_take_the_turn_of_a_repository_from_the_older_problem()
    {
        var problems = new InMemoryProblemStore();
        Add(problems, "older", FlowPriority.Normal, 0);
        Add(problems, "raised", FlowPriority.FirstUrgent, 5);

        MissionRunnerEndpoints.IsItsTurn(MissionOf("m1", "older", World.Release), problems).Should().BeTrue("a change that is half way to production keeps the repository");
        MissionRunnerEndpoints.IsItsTurn(MissionOf("m2", "raised", World.Staging), problems).Should().BeFalse();
    }
}
