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
        var stop = new SqliteKillSwitch(_file.Open(), auditCapacity: 1);
        await stop.ChangeAsync(true, Engaged(), Ct);
        await stop.ChangeAsync(false, Released(), Ct);
        _file.Reopen();

        (await stop.ChangeAsync(true, Engaged(), Ct)).Should().Be(KillSwitchChange.AppliedUntraced);

        stop.Engaged.Should().BeTrue();
        stop.Saved.Should().BeTrue("the file holds it, only the trace is missing");
        new SqliteKillSwitch(_file.Reopen(), auditCapacity: 1).Engaged.Should().BeTrue();
    }

    [Fact]
    public async Task An_unwritable_file_still_engages_and_never_releases()
    {
        var stop = new SqliteKillSwitch(_file.Open(), StoreLimits.FileAudit);
        _file.Damage();

        (await stop.ChangeAsync(true, Engaged(), Ct)).Should().Be(KillSwitchChange.AppliedUntraced);
        stop.Saved.Should().BeFalse();
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

        stop.Saved.Should().BeTrue();
    }

    [Fact]
    public async Task A_file_that_vanishes_is_an_error_and_not_a_new_database()
    {
        var stop = new SqliteKillSwitch(_file.Open(), StoreLimits.FileAudit);
        await stop.ChangeAsync(true, Engaged(), Ct);
        _file.Repair();

        stop.Engaged.Should().BeTrue("a file that is not there cannot say the stop was released");
        File.Exists(_file.Path).Should().BeFalse("nothing creates a database behind the host's back");
    }

    [Fact]
    public void The_file_is_created_for_its_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _file.Open();

        File.GetUnixFileMode(_file.Path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
