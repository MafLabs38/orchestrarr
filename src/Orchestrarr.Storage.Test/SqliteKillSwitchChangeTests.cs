using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Storage.Test;

/// <summary>A change of the stop and its trace are one write: whatever fails, the file never says "released" without the trace of it.</summary>
public sealed class SqliteKillSwitchChangeTests : IDisposable
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Noon = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDatabase _file = new();

    public void Dispose() => _file.Dispose();

    private static AuditEntry Engaged() => new(Noon, AuditEvents.KillSwitchEngaged, "alice-1", null, null);

    private static AuditEntry Released() => new(Noon.AddMinutes(1), AuditEvents.KillSwitchReleased, "alice-1", null, null);

    [Fact]
    public async Task Engaging_saves_the_stop_and_its_trace_together()
    {
        var database = _file.Open();
        var stop = new SqliteKillSwitch(database, StoreLimits.FileAudit);

        (await stop.ChangeAsync(true, Engaged(), Ct)).Should().Be(KillSwitchChange.Applied);

        new SqliteKillSwitch(_file.Reopen(), StoreLimits.FileAudit).Engaged.Should().BeTrue();
        new SqliteAuditLog(_file.Open()).List().Should().ContainSingle().Which.Event.Should().Be(AuditEvents.KillSwitchEngaged);
    }

    [Fact]
    public async Task Releasing_saves_the_release_and_its_trace_together()
    {
        var stop = new SqliteKillSwitch(_file.Open(), StoreLimits.FileAudit);
        await stop.ChangeAsync(true, Engaged(), Ct);

        (await stop.ChangeAsync(false, Released(), Ct)).Should().Be(KillSwitchChange.Applied);

        new SqliteKillSwitch(_file.Reopen(), StoreLimits.FileAudit).Engaged.Should().BeFalse();
        new SqliteAuditLog(_file.Open()).List().Select(entry => entry.Event).Should().Equal(AuditEvents.KillSwitchReleased, AuditEvents.KillSwitchEngaged);
    }

    [Fact]
    public async Task A_release_that_cannot_be_traced_changes_nothing_in_the_file()
    {
        var stop = new SqliteKillSwitch(_file.Open(), auditCapacity: 1);
        await stop.ChangeAsync(true, Engaged(), Ct);

        (await stop.ChangeAsync(false, Released(), Ct)).Should().Be(KillSwitchChange.NotTraced);

        new SqliteKillSwitch(_file.Reopen(), auditCapacity: 1).Engaged.Should().BeTrue();
        new SqliteAuditLog(_file.Open(), 1).List().Should().ContainSingle();
    }

    [Fact]
    public async Task An_engagement_that_cannot_be_traced_still_takes_effect_and_is_saved()
    {
        var database = _file.Open();
        var stop = new SqliteKillSwitch(database, auditCapacity: 1);
        using (var connection = database.Connect())
        {
            SqliteAuditLog.Insert(connection, Released());
        }
        stop.Engaged.Should().BeFalse("the trail is full, and the stop is released");

        (await stop.ChangeAsync(true, Engaged(), Ct)).Should().Be(KillSwitchChange.AppliedUntraced);

        stop.Engaged.Should().BeTrue();
        stop.State().Saved.Should().BeTrue("the file holds it, only the trace is missing");
        new SqliteKillSwitch(_file.Reopen(), auditCapacity: 1).Engaged.Should().BeTrue();
    }

    [Fact]
    public async Task An_unwritable_file_still_engages_and_never_releases()
    {
        var stop = new SqliteKillSwitch(_file.Open(), StoreLimits.FileAudit);
        _file.Damage();

        (await stop.ChangeAsync(true, Engaged(), Ct)).Should().Be(KillSwitchChange.AppliedUntraced);
        stop.State().Saved.Should().BeFalse();
        stop.State().Should().Be(new KillSwitchState(Engaged: true, Saved: false), "held in memory: engaged, and a restart would not keep it");
        (await stop.ChangeAsync(false, Released(), Ct)).Should().Be(KillSwitchChange.NotSaved);
        stop.Engaged.Should().BeTrue();
    }

    [Fact]
    public async Task A_saved_engagement_clears_what_was_held_in_memory()
    {
        var stop = new SqliteKillSwitch(_file.Open(), StoreLimits.FileAudit);
        _file.Damage();
        await stop.ChangeAsync(true, Engaged(), Ct);
        _file.Repair();
        _file.Open();

        (await stop.ChangeAsync(true, Engaged(), Ct)).Should().Be(KillSwitchChange.Applied);

        stop.State().Saved.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_file_that_vanishes_is_an_error_and_not_a_new_database_whatever_the_stop_was(bool engagedBefore)
    {
        var stop = new SqliteKillSwitch(_file.Open(), StoreLimits.FileAudit);
        await stop.ChangeAsync(true, Engaged(), Ct);
        if (!engagedBefore)
        {
            await stop.ChangeAsync(false, Released(), Ct);
        }
        _file.Vanish();

        stop.Engaged.Should().BeTrue("a file that is not there cannot say the stop was released");
        stop.State().Saved.Should().BeFalse("and the state is unknown");
        (await stop.ChangeAsync(false, Released(), Ct)).Should().Be(KillSwitchChange.NotSaved);
        File.Exists(_file.Path).Should().BeFalse("nothing creates a database behind the host's back");
    }

    [Fact]
    public async Task A_trail_filled_by_decisions_still_traces_the_stop_and_its_release()
    {
        var database = _file.Open();
        var trail = new SqliteAuditLog(database, 100);
        for (var n = 0; n < 95; n++)
        {
            (await trail.TryRecordAsync(new AuditEntry(Noon.AddSeconds(n), AuditEvents.ApprovalRecorded, "alice-1", null, null), Ct)).Should().BeTrue();
        }
        (await trail.TryRecordAsync(new AuditEntry(Noon, AuditEvents.ApprovalRecorded, "alice-1", null, null), Ct)).Should().BeFalse("the last places are the stop's");
        var stop = new SqliteKillSwitch(database, 100);

        (await stop.ChangeAsync(true, Engaged(), Ct)).Should().Be(KillSwitchChange.Applied);
        (await stop.ChangeAsync(false, Released(), Ct)).Should().Be(KillSwitchChange.Applied);

        trail.List().Should().HaveCount(97);
    }

    [Fact]
    public async Task A_held_engagement_is_released_once_the_file_is_back()
    {
        var stop = new SqliteKillSwitch(_file.Open(), StoreLimits.FileAudit);
        _file.Damage();
        await stop.ChangeAsync(true, Engaged(), Ct);
        _file.Repair();
        _file.Open();

        (await stop.ChangeAsync(false, Released(), Ct)).Should().Be(KillSwitchChange.Applied);

        stop.Engaged.Should().BeFalse();
        stop.State().Saved.Should().BeTrue();
    }

    [Fact]
    public async Task A_release_that_cannot_be_traced_keeps_a_held_engagement()
    {
        var stop = new SqliteKillSwitch(_file.Open(), auditCapacity: 1);
        _file.Damage();
        await stop.ChangeAsync(true, Engaged(), Ct);
        _file.Repair();
        var database = _file.Open();
        using (var connection = database.Connect())
        {
            SqliteAuditLog.Insert(connection, Engaged());
        }

        (await stop.ChangeAsync(false, Released(), Ct)).Should().Be(KillSwitchChange.NotTraced);

        stop.Engaged.Should().BeTrue();
        stop.State().Saved.Should().BeFalse();
    }

    [Fact]
    public async Task A_cancelled_release_changes_nothing_and_a_cancelled_engagement_still_engages()
    {
        var stop = new SqliteKillSwitch(_file.Open(), StoreLimits.FileAudit);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        (await stop.ChangeAsync(true, Engaged(), cancelled.Token)).Should().Be(KillSwitchChange.Applied);
        var release = async () => await stop.ChangeAsync(false, Released(), cancelled.Token);

        await release.Should().ThrowAsync<OperationCanceledException>();
        stop.Engaged.Should().BeTrue();
    }
}
