using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

public sealed class MissionOrderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);

    private static Mission MissionOf(string id, string problem, World world) =>
        new(id, problem, "app", world, "o", new MissionFrame(["o/r"], [], ""), new MissionBudget(1, 1, 1), MissionState.Mandated);

    private static void Add(InMemoryProblemStore store, string problem, int priority) =>
        store.Save(new ProblemRun(problem, "app", ProblemState.InProgress, T0, new PlanEntry(problem, "app", "c", "o", ["o/r"], World.Release, new MissionBudget(1, 1, 1), new Validation("x", null), T0, Priority: priority),
            World.Release, true, null!));

    [Fact]
    public void The_more_urgent_problem_is_offered_before_a_later_world_of_a_normal_one()
    {
        var problems = new InMemoryProblemStore();
        Add(problems, "normal", FlowPriority.Normal);
        Add(problems, "hot", FlowPriority.FirstUrgent);

        new[] { MissionOf("m1", "normal", World.Release), MissionOf("m2", "hot", World.Staging) }.FirstNext(problems)!.Id.Should().Be("m2");
    }

    [Fact]
    public void At_the_same_priority_the_later_world_goes_first_then_the_identifier()
    {
        var problems = new InMemoryProblemStore();
        Add(problems, "a", FlowPriority.Normal);
        Add(problems, "b", FlowPriority.Normal);

        new[] { MissionOf("m2", "a", World.Staging), MissionOf("m3", "b", World.Git), MissionOf("m1", "b", World.Git) }.FirstNext(problems)!.Id.Should().Be("m1");
    }
}
