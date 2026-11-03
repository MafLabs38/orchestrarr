using Microsoft.Data.Sqlite;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Storage.Test;

public sealed class SqliteAuditLogTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private readonly TempDatabase _file = new();

    public void Dispose() => _file.Dispose();

    private static AuditEntry Decision(int n = 0) => new(Noon.AddSeconds(n), AuditEvents.ApprovalRecorded, "alice-1", $"order-{n}", "a detail");

    private static AuditEntry Refusal(int n = 0) => new(Noon.AddSeconds(n), AuditEvents.ApprovalRefused, "alice-1", null, "stepNotGranted");

    [Fact]
    public async Task An_entry_survives_a_restart_with_everything_it_said()
    {
        (await new SqliteAuditLog(_file.Open()).TryRecordAsync(Decision(1), Ct)).Should().BeTrue();

        var entry = new SqliteAuditLog(_file.Open()).List().Should().ContainSingle().Which;

        entry.At.Should().Be(Noon.AddSeconds(1));
        entry.Event.Should().Be(AuditEvents.ApprovalRecorded);
        entry.Subject.Should().Be("alice-1");
        entry.OrderId.Should().Be("order-1");
        entry.Detail.Should().Be("a detail");
    }

    [Fact]
    public async Task An_entry_without_an_order_or_a_detail_keeps_none()
    {
        var log = new SqliteAuditLog(_file.Open());
        await log.TryRecordAsync(new AuditEntry(Noon, AuditEvents.KillSwitchEngaged, "alice-1", null, null), Ct);

        var entry = log.List().Single();

        entry.OrderId.Should().BeNull();
        entry.Detail.Should().BeNull();
    }

    [Fact]
    public async Task The_newest_comes_first()
    {
        var log = new SqliteAuditLog(_file.Open());
        await log.TryRecordRangeAsync([Decision(1), Decision(2), Decision(3)], Ct);

        log.List().Select(entry => entry.OrderId).Should().Equal("order-3", "order-2", "order-1");
    }

    [Fact]
    public async Task A_batch_that_does_not_fit_leaves_nothing()
    {
        var log = new SqliteAuditLog(_file.Open(), capacity: 3);
        (await log.TryRecordAsync(Decision(1), Ct)).Should().BeTrue();

        (await log.TryRecordRangeAsync([Decision(2), Decision(3), Decision(4)], Ct)).Should().BeFalse();

        log.List().Should().ContainSingle();
    }

    [Fact]
    public async Task A_trail_with_no_room_refuses_everything_so_that_nothing_happens_without_a_trace()
    {
        var log = new SqliteAuditLog(_file.Open(), capacity: 0);

        (await log.TryRecordAsync(Decision(), Ct)).Should().BeFalse();

        log.List().Should().BeEmpty();
    }

    [Fact]
    public void A_negative_capacity_is_refused()
    {
        var database = _file.Open();

        var build = () => new SqliteAuditLog(database, capacity: -1);

        build.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Refusals_have_a_smaller_share_so_that_real_decisions_still_fit()
    {
        var log = new SqliteAuditLog(_file.Open(), capacity: 8);

        for (var n = 0; n < 6; n++)
        {
            (await log.TryRecordAsync(Refusal(n), Ct)).Should().BeTrue();
        }

        (await log.TryRecordAsync(Refusal(6), Ct)).Should().BeFalse();
        (await log.TryRecordAsync(Decision(7), Ct)).Should().BeTrue();
        (await log.TryRecordAsync(Decision(8), Ct)).Should().BeFalse("the last place is the stop's");
    }

    [Fact]
    public async Task The_trail_is_append_only_the_database_refuses_to_change_or_remove_an_entry()
    {
        var database = _file.Open();
        await new SqliteAuditLog(database).TryRecordAsync(Decision(), Ct);

        using var connection = database.Connect();
        var change = () => SqliteDatabase.Run(connection, "UPDATE audit SET subject = 'someone-else';");
        var remove = () => SqliteDatabase.Run(connection, "DELETE FROM audit;");

        change.Should().Throw<SqliteException>().WithMessage("*append-only*");
        remove.Should().Throw<SqliteException>().WithMessage("*append-only*");
        new SqliteAuditLog(database).List().Single().Subject.Should().Be("alice-1");
    }

    [Fact]
    public async Task An_entry_that_cannot_be_written_is_said_not_recorded_and_never_lost_silently()
    {
        var log = new SqliteAuditLog(_file.Open());
        _file.Damage();

        (await log.TryRecordAsync(Decision(), Ct)).Should().BeFalse();
    }
}
