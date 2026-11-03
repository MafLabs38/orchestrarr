using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Storage.Test;

public sealed class SqliteNewDatabaseTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDatabase _file = new();

    public void Dispose() => _file.Dispose();

    private SqliteDatabase OpenEngaged() => SqliteDatabase.Open(_file.Path, startEngagedWhenNew: true, new FixedClock(Noon));

    [Fact]
    public void A_database_created_to_start_engaged_has_the_stop_engaged_and_says_why_in_the_trail()
    {
        var database = OpenEngaged();

        database.IsNew.Should().BeTrue();
        new SqliteKillSwitch(database, StoreLimits.FileAudit).Engaged.Should().BeTrue();
        var entry = new SqliteAuditLog(database).List().Should().ContainSingle().Which;
        entry.Event.Should().Be(AuditEvents.InstanceCreated);
        entry.Subject.Should().Be(AuditEvents.System);
        entry.At.Should().Be(Noon);
    }

    [Fact]
    public void A_database_created_for_the_sandbox_starts_released_and_has_no_trace()
    {
        var database = SqliteDatabase.Open(_file.Path);

        database.IsNew.Should().BeTrue();
        new SqliteKillSwitch(database, StoreLimits.FileAudit).Engaged.Should().BeFalse();
        new SqliteAuditLog(database).List().Should().BeEmpty();
    }

    [Fact]
    public void The_stop_that_was_released_stays_released_when_the_database_is_opened_again()
    {
        var database = OpenEngaged();
        new SqliteKillSwitch(database, StoreLimits.FileAudit).Release();

        var again = SqliteDatabase.Open(_file.Path, startEngagedWhenNew: true, new FixedClock(Noon));

        again.IsNew.Should().BeFalse();
        new SqliteKillSwitch(again, StoreLimits.FileAudit).Engaged.Should().BeFalse();
        new SqliteAuditLog(again).List().Count(entry => entry.Event == AuditEvents.InstanceCreated).Should().Be(1, "the creation is traced once, and opening again does not trace it again");
    }

    [Fact]
    public void The_database_refuses_a_state_of_the_stop_that_is_neither_engaged_nor_released()
    {
        var database = _file.Open();
        using var connection = database.Connect();

        var write = () => SqliteDatabase.Run(connection, "INSERT INTO kill_switch (id, engaged) VALUES (1, 7);");

        write.Should().Throw<SqliteException>();
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void The_folder_is_for_its_owner_only()
    {
        var folder = Path.Combine(Path.GetDirectoryName(_file.Path)!, "made", "by-the-host");

        SqliteDatabase.Open(Path.Combine(folder, "o.db"));

        File.GetUnixFileMode(folder).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.GetUnixFileMode(Path.Combine(folder, "o.db")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void A_file_of_version_zero_that_already_holds_the_tables_starts_engaged_when_asked_to()
    {
        var released = new SqliteKillSwitch(_file.Open(), StoreLimits.FileAudit);
        released.Release();
        using (var connection = _file.Open().Connect())
        {
            SqliteDatabase.Run(connection, "PRAGMA user_version = 0;");
        }

        var database = SqliteDatabase.Open(_file.Path, startEngagedWhenNew: true, new FixedClock(Noon));

        database.IsNew.Should().BeTrue();
        new SqliteKillSwitch(database, StoreLimits.FileAudit).Engaged.Should().BeTrue("the trace says the stop starts engaged, so it is");
        new SqliteAuditLog(database).List().Count(entry => entry.Event == AuditEvents.InstanceCreated).Should().Be(1);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
