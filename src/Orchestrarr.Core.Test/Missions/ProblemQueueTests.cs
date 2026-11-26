using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

public sealed class ProblemQueueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);

    private static QueuedProblem Problem(string id, string repository, int minutes = 0) =>
        new($"row-{id}", new PlanEntry(id, "app", "dependency-update", "x", [repository], World.Release, new MissionBudget(30, 3, 50), new Validation("alice", null), T0, 1, "abc"), T0.AddMinutes(minutes));

    private static QueuedProblem OfKind(string id, string repository, string kind, int minutes, string? nature = null) =>
        new($"row-{id}", new PlanEntry(id, "app", "x", "x", [repository], World.Release, new MissionBudget(30, 3, 50), new Validation("alice", null), T0, null, null, new ProblemBrief(kind, repository, "d", true, Nature: nature)), T0.AddMinutes(minutes));

    [Fact]
    public void A_repository_goes_through_its_route_whatever_the_order_of_the_ticks()
    {
        var line = new ProblemQueue();
        line.Enqueue(OfKind("bump", "o/one", "DependencyUpdate", 0));
        line.Enqueue(OfKind("alert", "o/one", "DependencyAlert", 1));
        line.Enqueue(OfKind("image", "o/one", "ImageUpdate", 2, "BaseImageUpdate"));
        line.Enqueue(OfKind("tag", "o/one", "FloatingImage", 3));
        line.Enqueue(OfKind("sidecar", "o/one", "ImageUpdate", 4, "ImageBehind"));
        line.Enqueue(OfKind("docker-bump", "o/one", "DependencyUpdate", 5, "BaseImageUpdate"));
        line.Enqueue(OfKind("red", "o/one", "FailingCi", 6));
        var order = new List<string>();

        for (var turn = 0; turn < 7; turn++)
        {
            order.AddRange(line.TakeReady(_ => false).Select(problem => problem.Entry.ProblemId));
        }

        // A red pipeline first, then the base image (whoever raises it), the alerts, the updates, and the rest (an image that is not a base, a moving tag)
        order.Should().Equal("red", "image", "docker-bump", "alert", "bump", "tag", "sidecar");
    }

    [Fact]
    public void A_lot_that_becomes_free_while_the_line_is_read_never_lets_a_later_operation_of_its_route_go_first()
    {
        var line = new ProblemQueue();
        line.Enqueue(OfKind("alert", "o/one", "DependencyAlert", 0));
        line.Enqueue(OfKind("bump", "o/one", "DependencyUpdate", 1));
        var asked = 0;

        // the operation before ends while the pass runs: busy when the line asks first, free afterwards
        var ready = line.TakeReady(_ => asked++ == 0);

        ready.Should().BeEmpty("the lot is busy for the whole pass, as it was when the pass asked");
        line.TakeReady(_ => false).Select(problem => problem.Entry.ProblemId).Should().Equal("alert");
    }

    [Fact]
    public void The_priority_a_person_sets_goes_before_the_route_and_is_bounded()
    {
        var line = new ProblemQueue();
        line.Enqueue(OfKind("image", "o/one", "ImageUpdate", 0));
        line.Enqueue(OfKind("bump", "o/one", "DependencyUpdate", 1));

        line.SetPriority("bump", 250).Should().BeTrue();
        line.SetPriority("absent", 60).Should().BeFalse();

        line.Ordered().Select(problem => (problem.Entry.ProblemId, problem.Priority)).Should().Equal(("bump", FlowPriority.Highest), ("image", FlowPriority.Normal));
        line.TakeReady(_ => false).Select(problem => problem.Entry.ProblemId).Should().Equal("bump");
    }

    [Fact]
    public void Two_operations_of_the_same_rank_keep_their_order_of_arrival()
    {
        var line = new ProblemQueue();
        line.Enqueue(OfKind("second", "o/one", "DependencyAlert", 5));
        line.Enqueue(OfKind("first", "o/one", "DependencyAlert", 1));

        line.TakeReady(_ => false).Select(problem => problem.Entry.ProblemId).Should().Equal("first");
    }

    [Fact]
    public void A_problem_that_cannot_start_does_not_hold_its_lot_and_the_next_of_the_route_takes_the_turn()
    {
        var line = new ProblemQueue();
        line.Enqueue(OfKind("image", "o/one", "ImageUpdate", 0));
        line.Enqueue(OfKind("bump", "o/one", "DependencyUpdate", 1));

        var ready = line.TakeReady(_ => false, waiting => waiting.Entry.ProblemId != "image");

        ready.Select(problem => problem.Entry.ProblemId).Should().Equal("bump");
        line.Waiting().Select(problem => problem.Entry.ProblemId).Should().Equal("image");
    }

    [Fact]
    public void A_parked_problem_is_not_ahead_of_a_new_one_and_the_entry_of_a_waiting_problem_can_follow_its_head()
    {
        var line = new ProblemQueue();
        line.Enqueue(OfKind("parked", "o/one", "DependencyUpdate", 0));

        line.Waits("o/one").Should().BeTrue();
        line.Waits("o/one", waiting => waiting.Entry.ProblemId == "parked").Should().BeFalse();
        var followed = line.Waiting()[0].Entry with { HeadSha = "new" };
        line.Replace("parked", followed).Should().BeTrue();
        line.Waiting()[0].Entry.HeadSha.Should().Be("new");
        line.Replace("absent", followed).Should().BeFalse();
    }

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

    [Fact]
    public void A_problem_that_leaves_is_gone_and_the_line_can_be_emptied_with_what_it_held_returned()
    {
        var line = new ProblemQueue();
        line.Enqueue(Problem("a", "o/one"));
        line.Enqueue(Problem("b", "o/one", 1));
        line.Enqueue(Problem("c", "o/two", 2));

        line.Remove("a").Should().BeTrue();
        line.Remove("a").Should().BeFalse();
        line.Contains("a").Should().BeFalse();
        line.Clear().Select(problem => problem.Entry.ProblemId).Should().BeEquivalentTo("b", "c");
        line.Waiting().Should().BeEmpty();
    }

    [Fact]
    public void A_problem_that_is_not_on_exactly_one_repository_is_never_ready()
    {
        var line = new ProblemQueue();
        var many = Problem("many", "o/one");
        line.Enqueue(many with { Entry = many.Entry with { Repositories = ["o/one", "o/two"] } });

        line.TakeReady(_ => false).Should().BeEmpty();
    }
}
