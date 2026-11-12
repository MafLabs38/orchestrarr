using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Core.Test.Decisions;

public sealed class ProblemHistoryTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private static HistoryEntry Skip(string row, string target) =>
        new(0, row, target, HistoryState.Skipped, "example/shop", ProblemKind.DependencyUpdate, target, null, null, null, null, null, Noon, "alice", null);

    [Fact]
    public void A_skip_holds_for_its_target_only_and_a_new_target_is_another_problem()
    {
        var history = new InMemoryProblemHistory();
        history.Record(Skip("image:n8n", "n8n 1.108.2 → 1.123.94"));

        var skips = history.ActiveSkips();

        skips.Should().Contain(new ActiveSkip("image:n8n", "n8n 1.108.2 → 1.123.94"));
        skips.Should().NotContain(new ActiveSkip("image:n8n", "n8n 1.108.2 → 1.123.95"));
    }

    [Fact]
    public void The_memory_keeps_the_same_promises_as_the_file()
    {
        var history = new InMemoryProblemHistory();
        var first = history.Record(Skip("a", "x"));
        history.Record(Skip("b", "y"));

        history.Page(null, 10, null).Select(line => line.RowId).Should().Equal("b", "a");
        history.Page(null, 10, history.Page(null, 1, null)[0].Id).Select(line => line.RowId).Should().Equal("a");
        history.UndoSkip(first, Noon).Should().BeTrue();
        history.UndoSkip(first, Noon).Should().BeFalse();
        history.ActiveSkips().Should().ContainSingle().Which.RowId.Should().Be("b");
    }

    [Theory]
    [InlineData(ApprovalState.Waiting, HistoryOutcome.InProgress)]
    [InlineData(ApprovalState.Taken, HistoryOutcome.InProgress)]
    [InlineData(ApprovalState.Refused, HistoryOutcome.Refused)]
    [InlineData(ApprovalState.Expired, HistoryOutcome.Expired)]
    public void An_approval_that_has_not_ended_or_did_not_run_says_so(ApprovalState state, HistoryOutcome expected)
    {
        HistoryOutcomes.Of(state, []).Should().Be(expected);
    }

    [Fact]
    public void An_approval_that_ended_is_a_repair_before_a_failure_before_a_pass_and_otherwise_just_ended()
    {
        HistoryOutcomes.Of(ApprovalState.DryRunDone, [TaskResult.Failed, TaskResult.Repaired]).Should().Be(HistoryOutcome.Repaired);
        HistoryOutcomes.Of(ApprovalState.DryRunDone, [TaskResult.Passed, TaskResult.Failed]).Should().Be(HistoryOutcome.Failed);
        HistoryOutcomes.Of(ApprovalState.DryRunDone, [TaskResult.Passed, TaskResult.DryRun]).Should().Be(HistoryOutcome.Passed);
        HistoryOutcomes.Of(ApprovalState.DryRunDone, [TaskResult.Passed, TaskResult.Refused]).Should().Be(HistoryOutcome.Refused);
        HistoryOutcomes.Of(ApprovalState.DryRunDone, [TaskResult.DryRun]).Should().Be(HistoryOutcome.Ended);
        HistoryOutcomes.Of(ApprovalState.DryRunDone, []).Should().Be(HistoryOutcome.Ended);
        HistoryOutcomes.Of(null, []).Should().Be(HistoryOutcome.InProgress);
    }
}
