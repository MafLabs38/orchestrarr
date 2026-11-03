using Microsoft.Data.Sqlite;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Storage.Test;

public sealed class SqliteApprovalLedgerTests : IDisposable
{
    private static readonly string Head = new('a', 40);
    private static readonly DateTimeOffset Noon = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private readonly TempDatabase _file = new();

    public void Dispose() => _file.Dispose();

    private static Approval Approval(int pullRequest = 7) =>
        new(Guid.NewGuid(), "repository:example/shop", "example/shop", pullRequest, Head, [StepKind.Validate], "alice-1", Noon, null);

    private static AuditEntry Entry(Approval approval) =>
        new(Noon, AuditEvents.ApprovalRecorded, approval.ApproverSubject, approval.Id.ToString("D"), $"example/shop#{approval.PullRequest}");

    private static AuditEntry Refusal() => new(Noon, AuditEvents.ApprovalRefused, "alice-1", null, "stepNotGranted");

    [Fact]
    public async Task The_approval_and_its_trace_are_written_together()
    {
        var database = _file.Open();
        var approval = Approval();

        var result = await new SqliteApprovalLedger(database, new InMemoryKillSwitch()).TryRecordAsync([approval], [Entry(approval)], Ct);

        result.Should().Be(LedgerResult.Recorded);
        new SqliteApprovalStore(database).List().Should().ContainSingle().Which.Id.Should().Be(approval.Id);
        new SqliteAuditLog(database).List().Should().ContainSingle().Which.OrderId.Should().Be(approval.Id.ToString("D"));
    }

    [Fact]
    public async Task A_decision_that_is_already_there_leaves_no_trace_of_the_second_attempt()
    {
        var database = _file.Open();
        var first = Approval();
        var again = first with { Id = Guid.NewGuid() };
        var ledger = new SqliteApprovalLedger(database, new InMemoryKillSwitch());
        (await ledger.TryRecordAsync([first], [Entry(first)], Ct)).Should().Be(LedgerResult.Recorded);

        var result = await ledger.TryRecordAsync([again], [Entry(again)], Ct);

        result.Should().Be(LedgerResult.AlreadyThere);
        new SqliteAuditLog(database).List().Should().ContainSingle("the trace of an approval that was not kept must not stay");
    }

    [Fact]
    public async Task A_batch_that_repeats_a_decision_is_refused_whole()
    {
        var database = _file.Open();
        var one = Approval(1);
        var same = one with { Id = Guid.NewGuid() };

        var result = await new SqliteApprovalLedger(database, new InMemoryKillSwitch()).TryRecordAsync([one, same], [Entry(one), Entry(same)], Ct);

        result.Should().Be(LedgerResult.AlreadyThere);
        new SqliteApprovalStore(database).List().Should().BeEmpty();
        new SqliteAuditLog(database).List().Should().BeEmpty();
    }

    [Fact]
    public async Task A_full_queue_refuses_and_leaves_no_trace()
    {
        var database = _file.Open();
        var ledger = new SqliteApprovalLedger(database, new InMemoryKillSwitch(), queueCapacity: 1);
        var first = Approval(1);
        (await ledger.TryRecordAsync([first], [Entry(first)], Ct)).Should().Be(LedgerResult.Recorded);
        var second = Approval(2);

        var result = await ledger.TryRecordAsync([second], [Entry(second)], Ct);

        result.Should().Be(LedgerResult.StoreFull);
        new SqliteAuditLog(database).List().Should().ContainSingle();
    }

    [Fact]
    public async Task A_full_trail_refuses_and_keeps_no_approval_without_its_trace()
    {
        var database = _file.Open();
        var approval = Approval();

        var result = await new SqliteApprovalLedger(database, new InMemoryKillSwitch(), auditCapacity: 0).TryRecordAsync([approval], [Entry(approval)], Ct);

        result.Should().Be(LedgerResult.Unavailable);
        new SqliteApprovalStore(database).List().Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_that_cannot_be_written_is_unavailable_and_nothing_is_said_recorded()
    {
        var ledger = new SqliteApprovalLedger(_file.Open(), new InMemoryKillSwitch());
        _file.Damage();
        var approval = Approval();

        (await ledger.TryRecordAsync([approval], [Entry(approval)], Ct)).Should().Be(LedgerResult.Unavailable);
    }

    [Fact]
    public async Task Two_batches_at_once_never_pass_the_capacity_and_exactly_one_wins()
    {
        var database = _file.Open();
        var tasks = new[] { 1, 2 }.Select(n => Task.Run(
            async () =>
            {
                var batch = new[] { Approval((n * 10) + 1), Approval((n * 10) + 2) };
                return await new SqliteApprovalLedger(database, new InMemoryKillSwitch(), queueCapacity: 2).TryRecordAsync(batch, [.. batch.Select(Entry)], Ct);
            },
            Ct));

        var results = await Task.WhenAll(tasks);

        results.Should().BeEquivalentTo([LedgerResult.Recorded, LedgerResult.StoreFull]);
        new SqliteApprovalStore(database).List().Should().HaveCount(2);
        new SqliteAuditLog(database).List().Should().HaveCount(2);
    }

    [Fact]
    public async Task A_locked_database_is_unavailable_and_leaves_nothing()
    {
        var database = SqliteDatabase.Open(_file.Path, busyTimeout: TimeSpan.FromMilliseconds(1));
        using var other = database.Connect();
        SqliteDatabase.Run(other, "BEGIN IMMEDIATE;");
        var approval = Approval();

        var result = await new SqliteApprovalLedger(database, new InMemoryKillSwitch()).TryRecordAsync([approval], [Entry(approval)], Ct);

        result.Should().Be(LedgerResult.Unavailable);
        SqliteDatabase.Run(other, "ROLLBACK;");
        new SqliteApprovalStore(database).List().Should().BeEmpty();
        new SqliteAuditLog(database).List().Should().BeEmpty();
    }

    [Fact]
    public async Task Refusals_in_the_batch_count_against_their_share_of_the_trail()
    {
        var database = _file.Open();
        var approval = Approval();
        var ledger = new SqliteApprovalLedger(database, new InMemoryKillSwitch(), auditCapacity: 4);

        // A capacity of 4 gives refusals 3 places: a batch of the approval and 4 refusals does not fit
        var result = await ledger.TryRecordAsync([approval], [Entry(approval), Refusal(), Refusal(), Refusal(), Refusal()], Ct);

        result.Should().Be(LedgerResult.Unavailable);
    }

    [Fact]
    public void A_capacity_that_makes_no_sense_is_refused()
    {
        var database = _file.Open();

        var queue = () => new SqliteApprovalLedger(database, new InMemoryKillSwitch(), queueCapacity: 0);
        var audit = () => new SqliteApprovalLedger(database, new InMemoryKillSwitch(), auditCapacity: -1);

        queue.Should().Throw<ArgumentOutOfRangeException>();
        audit.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Nothing_of_a_failed_attempt_was_left_by_the_connection_that_failed()
    {
        var database = _file.Open();

        var failing = () => database.InWriteTransaction<bool>(connection =>
        {
            SqliteApprovalStore.Insert(connection, Approval());
            throw new SqliteException("a failure after a write", 1);
        });

        failing.Should().Throw<SqliteException>().WithMessage("*a failure after a write*");
        new SqliteApprovalStore(database).List().Should().BeEmpty();
    }

    [Fact]
    public async Task An_engaged_stop_is_read_inside_the_write_and_nothing_is_recorded()
    {
        var database = _file.Open();
        using var stop = new InMemoryKillSwitch(startEngaged: true);
        var approval = Approval();

        var result = await new SqliteApprovalLedger(database, stop).TryRecordAsync([approval], [Entry(approval)], Ct);

        result.Should().Be(LedgerResult.Stopped);
        new SqliteApprovalStore(database).List().Should().BeEmpty();
        new SqliteAuditLog(database).List().Should().BeEmpty();
    }

    [Fact]
    public async Task The_stop_is_read_while_the_ledger_holds_the_write_lock_and_not_before()
    {
        var database = SqliteDatabase.Open(_file.Path, busyTimeout: TimeSpan.FromMilliseconds(1));
        var probe = new LockProbe(database);
        var approval = Approval();

        var result = await new SqliteApprovalLedger(database, probe).TryRecordAsync([approval], [Entry(approval)], Ct);

        result.Should().Be(LedgerResult.Recorded);
        probe.WriteLockWasHeldWhenRead.Should().BeTrue("an engagement cannot slip in between the read of the stop and the write");
    }

    [Fact]
    public async Task A_real_stop_engaged_in_the_file_refuses_the_write()
    {
        var database = _file.Open();
        var stop = new SqliteKillSwitch(database, StoreLimits.FileAudit);
        stop.Engage();
        var approval = Approval();

        var result = await new SqliteApprovalLedger(database, stop).TryRecordAsync([approval], [Entry(approval)], Ct);

        result.Should().Be(LedgerResult.Stopped);
        new SqliteApprovalStore(database).List().Should().BeEmpty();
    }

    // A stop that says whether another writer could have written at the moment it was read
    private sealed class LockProbe(SqliteDatabase database) : IKillSwitchControl
    {
        public bool WriteLockWasHeldWhenRead { get; private set; }

        public bool Engaged
        {
            get
            {
                using var other = database.Connect();
                try
                {
                    SqliteDatabase.Run(other, "BEGIN IMMEDIATE;");
                    SqliteDatabase.Run(other, "ROLLBACK;");
                }
                catch (SqliteException)
                {
                    WriteLockWasHeldWhenRead = true;
                }
                return false;
            }
        }

        public KillSwitchState State() => new(Engaged, Saved: true);

        public ValueTask<bool> IsEngagedAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Engaged);

        public ValueTask<KillSwitchChange> ChangeAsync(bool engaged, AuditEntry entry, CancellationToken cancellationToken) => ValueTask.FromResult(KillSwitchChange.Applied);
    }
}
