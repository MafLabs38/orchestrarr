using Microsoft.Data.Sqlite;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Storage.Test;

public sealed class SqliteApprovalStoreTests : IDisposable
{
    private static readonly string Head = new('a', 40);
    private static readonly DateTimeOffset Noon = new(2026, 11, 3, 12, 0, 0, TimeSpan.FromHours(1));

    private readonly TempDatabase _file = new();

    public void Dispose() => _file.Dispose();

    private static Approval Approval(int pullRequest = 7, string? head = null, StepKind[]? steps = null, int minutes = 0, DateTimeOffset? stepUp = null) =>
        new(Guid.NewGuid(), "repository:example/shop", "example/shop", pullRequest, head ?? Head, steps ?? [StepKind.Validate, StepKind.MergePullRequests], "alice-1", Noon.AddMinutes(minutes), stepUp);

    [Fact]
    public void An_approval_survives_a_restart_with_everything_it_said()
    {
        var approval = Approval(stepUp: Noon.AddMinutes(-3));
        new SeededStore(_file.Open()).TryAddRange([approval]).Should().BeTrue();

        var reopened = new SeededStore(_file.Open()).List().Should().ContainSingle().Which;

        reopened.Id.Should().Be(approval.Id);
        reopened.WorkItemId.Should().Be("repository:example/shop");
        reopened.Repository.Should().Be("example/shop");
        reopened.PullRequest.Should().Be(7);
        reopened.HeadSha.Should().Be(Head);
        reopened.Steps.Should().Equal(StepKind.Validate, StepKind.MergePullRequests);
        reopened.ApproverSubject.Should().Be("alice-1");
        reopened.ApprovedAt.Should().Be(Noon);
        reopened.StepUpAt.Should().Be(Noon.AddMinutes(-3));
    }

    [Fact]
    public void An_approval_without_a_second_factor_keeps_none()
    {
        var store = new SeededStore(_file.Open());
        store.TryAddRange([Approval()]).Should().BeTrue();

        store.List().Single().StepUpAt.Should().BeNull();
    }

    [Fact]
    public void The_newest_comes_first()
    {
        var store = new SeededStore(_file.Open());
        store.TryAddRange([Approval(pullRequest: 1, minutes: 0), Approval(pullRequest: 2, minutes: 10), Approval(pullRequest: 3, minutes: 5)]).Should().BeTrue();

        store.List().Select(approval => approval.PullRequest).Should().Equal(2, 3, 1);
    }

    [Fact]
    public void The_same_decision_is_recorded_once_whatever_the_order_of_its_steps()
    {
        var store = new SeededStore(_file.Open());
        var first = Approval(steps: [StepKind.Validate, StepKind.MergePullRequests]);
        var again = Approval(steps: [StepKind.MergePullRequests, StepKind.Validate]);
        store.TryAddRange([first]).Should().BeTrue();

        store.Contains(again).Should().BeTrue();
        store.TryAddRange([again]).Should().BeFalse();
        store.List().Should().ContainSingle();
    }

    [Fact]
    public void Another_commit_or_other_steps_or_another_pull_request_is_another_decision()
    {
        var store = new SeededStore(_file.Open());
        store.TryAddRange([Approval()]).Should().BeTrue();

        store.Contains(Approval(head: new string('b', 40))).Should().BeFalse();
        store.Contains(Approval(steps: [StepKind.Validate])).Should().BeFalse();
        store.Contains(Approval(pullRequest: 8)).Should().BeFalse();
        store.TryAddRange([Approval(head: new string('b', 40)), Approval(steps: [StepKind.Validate]), Approval(pullRequest: 8)]).Should().BeTrue();
    }

    [Fact]
    public void The_database_itself_refuses_a_second_copy_of_a_decision()
    {
        var database = _file.Open();
        var store = new SeededStore(database);
        store.TryAddRange([Approval()]).Should().BeTrue();

        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO approvals (id, work_item_id, repository, pull_request, head_sha, steps, approver, approved_at) VALUES ('x', 'w', 'example/shop', 7, $head, 'Validate,MergePullRequests', 'a', '2026-11-03T12:00:00.0000000Z');";
        command.Parameters.AddWithValue("$head", Head);
        var insert = () => command.ExecuteNonQuery();

        insert.Should().Throw<SqliteException>();
    }

    [Fact]
    public void A_batch_is_added_whole_or_not_at_all()
    {
        var store = new SeededStore(_file.Open());
        store.TryAddRange([Approval(pullRequest: 1)]).Should().BeTrue();

        var added = store.TryAddRange([Approval(pullRequest: 2), Approval(pullRequest: 1), Approval(pullRequest: 3)]);

        added.Should().BeFalse();
        store.List().Select(approval => approval.PullRequest).Should().Equal(1);
    }

    [Fact]
    public void A_full_store_refuses_and_never_forgets_one_to_make_room()
    {
        var store = new SeededStore(_file.Open(), capacity: 2);
        store.TryAddRange([Approval(pullRequest: 1)]).Should().BeTrue();
        store.Free.Should().Be(1);

        store.TryAddRange([Approval(pullRequest: 2), Approval(pullRequest: 3)]).Should().BeFalse();
        store.TryAddRange([Approval(pullRequest: 2)]).Should().BeTrue();

        store.Free.Should().Be(0);
        store.TryAddRange([Approval(pullRequest: 9)]).Should().BeFalse();
        store.List().Select(approval => approval.PullRequest).Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public void A_capacity_of_zero_or_less_is_refused()
    {
        var database = _file.Open();

        var build = () => new SeededStore(database, capacity: 0);

        build.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Two_at_once_for_the_same_decision_make_one()
    {
        var database = _file.Open();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => new SeededStore(database).TryAddRange([Approval()]), TestContext.Current.CancellationToken)));

        results.Count(added => added).Should().Be(1);
        new SeededStore(database).List().Should().ContainSingle();
    }

    [Fact]
    public void A_database_that_cannot_be_read_is_an_error_and_not_an_empty_store()
    {
        var store = new SeededStore(_file.Open());
        _file.Damage();

        var read = () => store.List();

        read.Should().Throw<StorageUnavailableException>();
    }

    [Fact]
    public void A_row_that_does_not_read_back_is_an_unusable_storage_and_not_an_empty_list_or_a_raw_error()
    {
        var database = _file.Open();
        var store = new SeededStore(database);
        store.TryAddRange([Approval()]).Should().BeTrue();
        using (var connection = database.Connect())
        {
            SqliteDatabase.Run(connection, "UPDATE approvals SET steps = 'NoSuchStep';");
        }

        var read = () => store.List();

        read.Should().Throw<StorageUnavailableException>();
    }
}
