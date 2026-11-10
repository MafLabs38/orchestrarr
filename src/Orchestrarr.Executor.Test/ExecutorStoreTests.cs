using Microsoft.Data.Sqlite;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor.Test;

public sealed class ExecutorStoreTests : IDisposable
{
    private readonly ExecutorFixture _world = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task An_enrolled_secret_is_found_and_a_stranger_has_none()
    {
        (await _world.Store.FindAsync("alice", Ct)).Should().Equal(ExecutorFixture.Secret);
        (await _world.Store.FindAsync("mallory", Ct)).Should().BeNull();
        _world.Store.Operators().Select(entry => entry.Subject).Should().Equal("alice");
    }

    [Fact]
    public async Task Enrolling_again_replaces_the_secret_and_a_removed_operator_is_gone()
    {
        _world.Store.Enrol("alice", [1, 2, 3], 5, ExecutorFixture.Start);

        (await _world.Store.FindAsync("alice", Ct)).Should().Equal(1, 2, 3);
        _world.Store.Remove("alice").Should().BeTrue();
        (await _world.Store.FindAsync("alice", Ct)).Should().BeNull();
        _world.Store.Remove("alice").Should().BeFalse();
    }

    [Fact]
    public async Task A_step_is_accepted_only_if_it_is_later_than_the_last_one()
    {
        (await _world.Store.TryAdvanceAsync("alice", 10, Ct)).Should().BeTrue();
        (await _world.Store.TryAdvanceAsync("alice", 10, Ct)).Should().BeFalse();
        (await _world.Store.TryAdvanceAsync("alice", 9, Ct)).Should().BeFalse();
        (await _world.Store.TryAdvanceAsync("alice", 11, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Two_requests_with_the_same_step_never_both_succeed()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => _world.Store.TryAdvanceAsync("alice", 50, Ct).AsTask(), Ct)));

        results.Count(accepted => accepted).Should().Be(1);
    }

    [Fact]
    public void A_batch_is_registered_once()
    {
        var id = Guid.NewGuid();

        _world.Store.TryRegisterBatch(id, ExecutorFixture.Start).Should().BeTrue();
        _world.Store.TryRegisterBatch(id, ExecutorFixture.Start).Should().BeFalse();
    }

    [Fact]
    public void What_is_registered_survives_a_restart_of_the_executor()
    {
        var id = Guid.NewGuid();
        _world.Store.TryRegisterBatch(id, ExecutorFixture.Start);

        var restarted = new ExecutorStore(Path.Combine(_world.Folder, "executor.db"));

        restarted.TryRegisterBatch(id, ExecutorFixture.Start).Should().BeFalse();
    }

    [Fact]
    public void A_quota_is_counted_per_day_all_or_nothing()
    {
        var id = Guid.NewGuid();
        var day = ExecutorFixture.Start;

        _world.Store.TryUseDelegation(id, 5, 3, day).Should().BeTrue();
        _world.Store.TryUseDelegation(id, 5, 3, day).Should().BeFalse("three more would make six");
        _world.Store.TryUseDelegation(id, 5, 2, day).Should().BeTrue("the refusal above used nothing");
        _world.Store.TryUseDelegation(id, 5, 1, day).Should().BeFalse();
        _world.Store.TryUseDelegation(id, 5, 5, day.AddDays(1)).Should().BeTrue();
    }

    [Fact]
    public void The_delegations_in_force_leave_out_the_revoked_and_the_expired()
    {
        var inForce = new Delegation(Guid.NewGuid(), "alice", TaskKind.MergePullRequest, "example/*", ExecutorFixture.Start.AddDays(5), 5, ExecutorFixture.Start, null);
        var revoked = inForce with { Id = Guid.NewGuid() };
        var expired = inForce with { Id = Guid.NewGuid(), ExpiresAt = ExecutorFixture.Start.AddDays(-1) };
        foreach (var delegation in new[] { inForce, revoked, expired })
        {
            _world.Store.AddDelegation(delegation);
        }
        _world.Store.RevokeDelegation(revoked.Id, ExecutorFixture.Start).Should().BeTrue();
        _world.Store.RevokeDelegation(revoked.Id, ExecutorFixture.Start).Should().BeFalse("it is revoked once");

        _world.Store.Delegations(includeEnded: false, ExecutorFixture.Start).Select(delegation => delegation.Id).Should().Equal(inForce.Id);
        _world.Store.Delegations(includeEnded: true).Should().HaveCount(3);
    }

    [Fact]
    public void The_trace_in_the_executors_file_cannot_be_changed_or_removed()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_world.Folder, "executor.db"), Pooling = false }.ToString());
        connection.Open();
        _world.Store.AppendTrace(Guid.NewGuid(), Guid.NewGuid(), RunEvent.Create("0", 1, ExecutorFixture.Start, RunEvents.Claimed, null, null));

        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE run_events SET detail = 'x';";
        var change = () => update.ExecuteNonQuery();
        change.Should().Throw<SqliteException>().WithMessage("*append-only*");
        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM run_events;";
        var remove = () => delete.ExecuteNonQuery();
        remove.Should().Throw<SqliteException>().WithMessage("*append-only*");
    }

    [Fact]
    public void The_acts_of_the_operators_in_the_executors_file_cannot_be_changed_or_removed()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_world.Folder, "executor.db"), Pooling = false }.ToString());
        connection.Open();
        _world.Store.RecordAct("enrolled", "alice", null, ExecutorFixture.Start);

        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE operator_acts SET subject = 'x';";
        var change = () => update.ExecuteNonQuery();
        change.Should().Throw<SqliteException>().WithMessage("*append-only*");
        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM operator_acts;";
        var remove = () => delete.ExecuteNonQuery();
        remove.Should().Throw<SqliteException>().WithMessage("*append-only*");
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void The_file_is_made_for_its_owner_only()
    {
        File.GetUnixFileMode(Path.Combine(_world.Folder, "executor.db")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

public sealed class RepositoryPatternTests
{
    [Theory]
    [InlineData("example/app", "example/app", true)]
    [InlineData("example/app", "Example/App", true)]
    [InlineData("example/app", "example/other", false)]
    [InlineData("example/*", "example/app", true)]
    [InlineData("example/*", "other/app", false)]
    [InlineData("*", "any/thing", true)]
    [InlineData("*/app", "example/app", true)]
    [InlineData("*/app", "example/app2", false)]
    [InlineData("ex*/a*p", "example/app", true)]
    [InlineData("ex*/a*p", "example/apple", false)]
    [InlineData("example/app", "example/app-extra", false)]
    [InlineData("example/app*", "example/app", true)]
    [InlineData("a*b*c", "axxbxxc", true)]
    [InlineData("a*b*c", "axxcxxb", false)]
    [InlineData("", "example/app", false)]
    [InlineData("example/app", "", false)]
    [InlineData("a**b", "axb", true)]
    [InlineData("ab*bc", "abc", false)]
    public void A_star_stands_for_any_characters_and_nothing_else_does(string pattern, string repository, bool expected)
    {
        TaskShape.Matches(pattern, repository).Should().Be(expected);
    }
}
