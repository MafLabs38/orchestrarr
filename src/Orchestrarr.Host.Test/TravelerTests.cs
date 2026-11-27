using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

public sealed class TravelerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly FlowProblem Shown = new("p-1", "nginx:1.14 → 1.28", "ImageUpdate", "o/one", "closed", FlowPriority.Normal, T0, [], null, null);

    private static Mission Mission(string id, World world) =>
        new(id, "p-1", "app", world, "o", new MissionFrame(["o/one"], [], ""), new MissionBudget(1, 1, 1), MissionState.Accepted);

    private static AuditRow Row(int id, string eventName, string subject, string? detail = null) => new(id, new AuditEntry(T0.AddMinutes(id), eventName, subject, null, detail));

    [Fact]
    public void The_traveler_tells_the_latest_run_step_by_step_with_the_world_of_each_mission_and_a_repeated_refusal_once()
    {
        var rows = new[]
        {
            Row(1, "problem.started", "p-1", "an earlier run"),
            Row(2, "problem.failed", "p-1", "the earlier run failed"),
            Row(3, "problem.queued", "p-1", "waits for its turn"),
            Row(4, "problem.started", "p-1"),
            Row(5, "mission.handed-over", "m-s"),
            Row(6, "act.refused", "m-g", "MergePullRequest: CiNotGreenOnExactCommit"),
            Row(7, "act.refused", "m-g", "MergePullRequest: CiNotGreenOnExactCommit"),
            Row(8, "act.refused", "m-g", "MergePullRequest: CiNotGreenOnExactCommit"),
            Row(9, "act.performed", "m-g", "merged"),
            Row(10, "problem.closed", "p-1", "done as the lead planned"),
        };

        var traveler = Traveler.Build(Shown, [Mission("m-s", World.Staging), Mission("m-g", World.Git)], rows);

        traveler.Title.Should().Be("nginx:1.14 → 1.28");
        traveler.Steps.Select(step => (step.Event, step.World, step.Times)).Should().Equal(
            ("problem.queued", null, 1), ("problem.started", null, 1), ("mission.handed-over", "Staging", 1), ("act.refused", "Git", 3), ("act.performed", "Git", 1), ("problem.closed", null, 1));
        traveler.Steps[^1].Text.Should().Be("done as the lead planned");
    }

    [Fact]
    public void A_long_detail_is_cut_and_a_run_that_has_just_ended_keeps_its_steps()
    {
        var rows = new[] { Row(1, "problem.started", "p-1", new string('x', 1000)), Row(2, "problem.escalated", "p-1", "a person decides") };

        var traveler = Traveler.Build(Shown, [], rows);

        traveler.Steps.Should().HaveCount(2, "the end of the latest run is part of it");
        traveler.Steps[0].Text.Should().HaveLength(400);
    }
}
