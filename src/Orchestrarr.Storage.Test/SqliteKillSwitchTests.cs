using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Storage.Test;

public sealed class SqliteKillSwitchTests : IDisposable
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private readonly TempDatabase _file = new();

    public void Dispose() => _file.Dispose();

    private SqliteKillSwitch Stop() => new(_file.Open(), StoreLimits.FileAudit);

    [Fact]
    public void A_new_database_starts_released()
    {
        Stop().Engaged.Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_means_engaged_a_state_that_cannot_be_read_counts_as_engaged_and_is_not_saved()
    {
        var stop = Stop();
        _file.Damage();

        stop.Engaged.Should().BeTrue();
        stop.State().Saved.Should().BeFalse("the state is unknown, so nothing says a restart keeps it");
        (await stop.IsEngagedAsync(Ct)).Should().BeTrue();
    }

    [Fact]
    public void Saved_read_alone_says_the_file_cannot_be_read_without_anyone_asking_for_the_stop_first()
    {
        var stop = Stop();
        _file.Damage();

        stop.State().Saved.Should().BeFalse();
    }

    [Fact]
    public void A_missing_row_is_unknown_and_unknown_is_engaged()
    {
        var database = _file.Open();
        var stop = new SqliteKillSwitch(database, StoreLimits.FileAudit);
        using (var connection = database.Connect())
        {
            SqliteDatabase.Run(connection, "DELETE FROM kill_switch;");
        }

        stop.Engaged.Should().BeTrue();
    }

    [Fact]
    public void A_missing_table_is_unknown_and_unknown_is_engaged()
    {
        var database = _file.Open();
        var stop = new SqliteKillSwitch(database, StoreLimits.FileAudit);
        using (var connection = database.Connect())
        {
            SqliteDatabase.Run(connection, "DROP TABLE kill_switch;");
        }

        stop.Engaged.Should().BeTrue();
    }

    [Fact]
    public void A_release_that_cannot_be_saved_leaves_the_stop_engaged()
    {
        var stop = Stop();
        stop.Engage();
        _file.Damage();

        stop.Release().Should().Be(KillSwitchChange.NotSaved);

        stop.Engaged.Should().BeTrue();
    }

    [Fact]
    public void A_release_that_fails_while_an_engagement_is_held_keeps_it_held()
    {
        var stop = Stop();
        _file.Damage();
        stop.Engage();

        stop.Release().Should().Be(KillSwitchChange.NotSaved);

        stop.Engaged.Should().BeTrue();
        stop.State().Saved.Should().BeFalse();
    }

    [Fact]
    public void A_locked_database_still_engages_cannot_release_and_still_reads()
    {
        var database = SqliteDatabase.Open(_file.Path, busyTimeout: TimeSpan.FromMilliseconds(1));
        var stop = new SqliteKillSwitch(database, StoreLimits.FileAudit);
        stop.Engage();
        using var other = database.Connect();
        SqliteDatabase.Run(other, "BEGIN IMMEDIATE;");

        var engage = stop.Engage();
        var release = stop.Release();

        engage.Should().Be(KillSwitchChange.AppliedUntraced);
        stop.State().Saved.Should().BeFalse("the file was locked, so the engagement is held in memory");
        release.Should().Be(KillSwitchChange.NotSaved);
        stop.Engaged.Should().BeTrue();
    }

    [Fact]
    public async Task Engaging_and_releasing_at_once_leave_a_stop_that_agrees_with_its_last_trace()
    {
        var database = SqliteDatabase.Open(_file.Path, busyTimeout: TimeSpan.FromSeconds(30));
        var stop = new SqliteKillSwitch(database, StoreLimits.FileAudit);
        var at = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(n => Task.Run(
            async () => await stop.ChangeAsync(n % 2 == 0, new AuditEntry(at, n % 2 == 0 ? AuditEvents.KillSwitchEngaged : AuditEvents.KillSwitchReleased, "alice-1", null, null), Ct),
            Ct)));

        results.Should().OnlyContain(result => result == KillSwitchChange.Applied);
        var trail = new SqliteAuditLog(database).List();
        trail.Should().HaveCount(8);
        stop.Engaged.Should().Be(trail[0].Event == AuditEvents.KillSwitchEngaged, "the stop is what the last entry of the trail says");
    }

    [Fact]
    public async Task Engaging_counts_at_once_while_another_writer_holds_the_file()
    {
        var database = SqliteDatabase.Open(_file.Path, busyTimeout: TimeSpan.FromSeconds(5));
        var stop = new SqliteKillSwitch(database, StoreLimits.FileAudit);
        using var other = database.Connect();
        SqliteDatabase.Run(other, "BEGIN IMMEDIATE;");

        var engage = Task.Run(async () => await stop.ChangeAsync(true, new AuditEntry(DateTimeOffset.UnixEpoch, AuditEvents.KillSwitchEngaged, "alice-1", null, null), Ct), Ct);

        SpinWait.SpinUntil(() => stop.Engaged, TimeSpan.FromSeconds(10)).Should().BeTrue("the stop does not wait for the lock");
        SqliteDatabase.Run(other, "ROLLBACK;");
        (await engage.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().Be(KillSwitchChange.Applied);
        stop.State().Saved.Should().BeTrue();
    }

    [Fact]
    public void The_state_is_one_read_for_both_answers()
    {
        var stop = Stop();
        stop.State().Should().Be(new KillSwitchState(Engaged: false, Saved: true));
        stop.Engage();
        stop.State().Should().Be(new KillSwitchState(Engaged: true, Saved: true));
        _file.Damage();

        stop.State().Should().Be(new KillSwitchState(Engaged: true, Saved: false), "an unreadable file is engaged and not saved");
    }

}
