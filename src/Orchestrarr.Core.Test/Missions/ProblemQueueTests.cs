using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

public sealed class ProblemQueueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);

    private static QueuedProblem Problem(string id, string repository, int minutes = 0) =>
        new($"row-{id}", new PlanEntry(id, "app", "dependency-update", "x", [repository], World.Release, new MissionBudget(30, 3, 50), new Validation("alice", null), T0, 1, "abc"), T0.AddMinutes(minutes));

    [Fact]
    public void The_same_problem_waits_once()
    {
        var line = new ProblemQueue();

        line.Enqueue(Problem("a", "o/r")).Should().BeTrue();
        line.Enqueue(Problem("a", "o/r", 1)).Should().BeFalse();

        line.Waiting().Should().ContainSingle();
    }

    [Fact]
    public void Only_the_first_problem_of_a_free_repository_is_ready_and_the_others_go_on_waiting()
    {
        var line = new ProblemQueue();
        line.Enqueue(Problem("a", "o/one", 0));
        line.Enqueue(Problem("b", "o/one", 1));
        line.Enqueue(Problem("c", "o/two", 2));

        var ready = line.TakeReady(_ => false);

        ready.Select(problem => problem.Entry.ProblemId).Should().Equal("a", "c");
        line.Waiting().Select(problem => problem.Entry.ProblemId).Should().Equal("b");
    }

    [Fact]
    public void A_busy_repository_keeps_its_problems_in_line()
    {
        var line = new ProblemQueue();
        line.Enqueue(Problem("a", "o/one"));
        line.Enqueue(Problem("c", "o/two", 1));

        var ready = line.TakeReady(repository => repository == "o/one");

        ready.Select(problem => problem.Entry.ProblemId).Should().Equal("c");
        line.Waits("o/one").Should().BeTrue();
        line.Waits("o/two").Should().BeFalse();
    }

    [Fact]
    public void The_line_keeps_the_order_in_which_the_problems_were_validated()
    {
        var line = new ProblemQueue();
        line.Enqueue(Problem("late", "o/one", 5));
        line.Enqueue(Problem("early", "o/one", 1));

        line.TakeReady(_ => false).Should().ContainSingle().Which.Entry.ProblemId.Should().Be("early");
    }
}
