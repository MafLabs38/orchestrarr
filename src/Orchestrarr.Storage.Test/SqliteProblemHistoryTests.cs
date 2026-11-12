using Orchestrarr.Core.Decisions;

namespace Orchestrarr.Storage.Test;

public sealed class SqliteProblemHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDatabase _file = new();

    public void Dispose() => _file.Dispose();

    private static HistoryEntry Entry(string row, HistoryState state, string description = "express 4.16.0 → 5.3.0", int minute = 0) =>
        new(0, row, description, state, "example/shop", ProblemKind.DependencyUpdate, description, "example/shop", 14, "abc", state == HistoryState.Approved ? Guid.NewGuid() : null, null,
            Noon.AddMinutes(minute), "alice", state == HistoryState.Skipped ? "not now" : null);

    [Fact]
    public void A_line_is_kept_with_everything_of_the_photograph_across_a_restart()
    {
        var history = new SqliteProblemHistory(_file.Open());
        var id = history.Record(Entry("pr:example/shop#14", HistoryState.Approved));

        var line = new SqliteProblemHistory(_file.Reopen()).Page(null, 10, null).Should().ContainSingle().Subject;

        line.Id.Should().Be(id);
        line.Should().BeEquivalentTo(Entry("pr:example/shop#14", HistoryState.Approved) with { Id = id, ApprovalId = line.ApprovalId });
        line.ApprovalId.Should().NotBeNull();
    }

    [Fact]
    public void The_newest_comes_first_a_page_continues_from_the_last_identifier_and_the_state_filters()
    {
        var history = new SqliteProblemHistory(_file.Open());
        for (var i = 0; i < 5; i++)
        {
            history.Record(Entry($"pr:example/shop#{i}", i % 2 == 0 ? HistoryState.Approved : HistoryState.Skipped, minute: i));
        }

        var first = history.Page(null, 2, null);
        var second = history.Page(null, 2, first[^1].Id);

        first.Select(line => line.RowId).Should().Equal("pr:example/shop#4", "pr:example/shop#3");
        second.Select(line => line.RowId).Should().Equal("pr:example/shop#2", "pr:example/shop#1");
        history.Page(HistoryState.Skipped, 10, null).Should().OnlyContain(line => line.State == HistoryState.Skipped).And.HaveCount(2);
    }

    [Fact]
    public void A_skip_is_in_force_until_it_is_taken_back_once_and_only_a_skip_can_be_taken_back()
    {
        var history = new SqliteProblemHistory(_file.Open());
        var skip = history.Record(Entry("pr:example/shop#14", HistoryState.Skipped));
        var approved = history.Record(Entry("pr:example/shop#15", HistoryState.Approved));

        history.ActiveSkips().Should().ContainSingle().Which.Should().Be(new ActiveSkip("pr:example/shop#14", "express 4.16.0 → 5.3.0"));
        history.UndoSkip(approved, Noon).Should().BeFalse("an approval is not undone");
        history.UndoSkip(skip, Noon).Should().BeTrue();
        history.UndoSkip(skip, Noon).Should().BeFalse("it was taken back already");

        history.ActiveSkips().Should().BeEmpty();
        history.Page(null, 10, null).Should().ContainSingle().Which.Id.Should().Be(approved);
    }

    [Fact]
    public void The_outcome_is_kept_once_it_is_known_across_a_restart_and_a_skip_has_its_own()
    {
        var history = new SqliteProblemHistory(_file.Open());
        var approved = history.Record(Entry("pr:example/shop#14", HistoryState.Approved));
        var skipped = history.Record(Entry("pr:example/shop#15", HistoryState.Skipped));

        history.Conclude(approved, HistoryOutcome.Passed);

        var lines = new SqliteProblemHistory(_file.Reopen()).Page(null, 10, null);
        lines.Single(line => line.Id == approved).Outcome.Should().Be(HistoryOutcome.Passed);
        lines.Single(line => line.Id == skipped).Outcome.Should().BeNull("a skip has no run to conclude; the screen says it was skipped");
    }

    [Fact]
    public void Only_the_approved_lines_without_an_outcome_are_waiting_to_be_concluded_oldest_first()
    {
        var history = new SqliteProblemHistory(_file.Open());
        var first = history.Record(Entry("pr:example/shop#1", HistoryState.Approved));
        var second = history.Record(Entry("pr:example/shop#2", HistoryState.Approved));
        history.Record(Entry("pr:example/shop#3", HistoryState.Skipped));
        history.Conclude(first, HistoryOutcome.Failed);

        history.Unconcluded(10).Select(line => line.Id).Should().Equal(second);
        history.Unconcluded(10, after: second).Should().BeEmpty("the pass moves on from the last line it looked at");
    }

    [Fact]
    public void A_line_that_cannot_be_read_is_left_out_without_cutting_the_pages_short()
    {
        var database = _file.Open();
        var history = new SqliteProblemHistory(database);
        var ids = Enumerable.Range(1, 3).Select(i => history.Record(Entry($"pr:example/shop#{i}", HistoryState.Approved, minute: i))).ToList();
        using (var connection = database.Connect())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE problem_history SET batch_id = 'not an identifier' WHERE id = $id;";
            command.Parameters.AddWithValue("$id", ids[1]);
            command.ExecuteNonQuery();
        }

        var scan = history.ReadPage(null, 3, null);

        scan.Lines.Select(line => line.Id).Should().Equal(ids[2], ids[0]);
        scan.Cursor.Should().Be(ids[0], "the page was full: there may be more behind it");
        history.ReadUnconcluded(3, 0).Cursor.Should().Be(ids[2]);
    }

    [Fact]
    public void A_skip_that_lapsed_is_no_longer_in_force_cannot_be_taken_back_and_stays_in_the_history()
    {
        var history = new SqliteProblemHistory(_file.Open());
        var skip = history.Record(Entry("pr:example/shop#14", HistoryState.Skipped));
        history.Record(Entry("pr:example/shop#15", HistoryState.Skipped));

        history.LapseSkips([new ActiveSkip("pr:example/shop#14", "express 4.16.0 → 5.3.0")]).Should().Be(1);

        history.ActiveSkips().Should().ContainSingle().Which.RowId.Should().Be("pr:example/shop#15");
        history.UndoSkip(skip, Noon).Should().BeFalse();
        history.Page(HistoryState.Skipped, 10, null).Single(line => line.Id == skip).Outcome.Should().Be(HistoryOutcome.Ended);
    }
}
