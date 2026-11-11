using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Storage.Test;

public sealed class SqliteBatchQueueTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Head = new('a', 40);
    private static readonly TaskOptions Options = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDatabase _file = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly InMemoryKillSwitch _stop = new();
    private readonly SqliteDatabase _database;
    private readonly SqliteBatchQueue _queue;
    private readonly SqliteApprovalLedger _ledger;
    private readonly SqliteApprovalStore _approvals;
    private readonly SqliteAuditLog _audit;

    public SqliteBatchQueueTests()
    {
        _database = _file.Open();
        _queue = new SqliteBatchQueue(_database, _stop, _time, Options);
        _ledger = new SqliteApprovalLedger(_database, _stop);
        _approvals = new SqliteApprovalStore(_database);
        _audit = new SqliteAuditLog(_database);
    }

    public void Dispose() => _file.Dispose();

    private async Task<TaskBatch> Queue(int pullRequest = 1, string code = "123456")
    {
        var batchId = Guid.NewGuid();
        var approval = new Approval(Guid.NewGuid(), "repository:example/app", "example/app", pullRequest, Head, [StepKind.Validate, StepKind.MergePullRequests], "alice", _time.GetUtcNow(), null, ApprovalState.Waiting, batchId);
        var batch = new TaskBatch(
            batchId, approval.WorkItemId, "alice", _time.GetUtcNow(), _time.GetUtcNow() + Options.BatchLifetime,
            [
                new TaskSpec(Guid.NewGuid(), 1, TaskKind.ValidatePullRequest, "example/app", pullRequest, Head, approval.Id),
                new TaskSpec(Guid.NewGuid(), 2, TaskKind.MergePullRequest, "example/app", pullRequest, Head, approval.Id),
            ]);
        (await _ledger.TryRecordAsync([approval], batch, code, [new AuditEntry(_time.GetUtcNow(), AuditEvents.BatchQueued, "alice", batchId.ToString("D"), null)], Ct)).Should().Be(LedgerResult.Recorded);
        return batch;
    }

    private static RunEvent[] Chain(TaskBatch batch, params string[] types)
    {
        var previous = batch.Digest();
        var lines = new List<RunEvent>();
        foreach (var type in types)
        {
            var line = RunEvent.Create(previous, lines.Count + 1, Start, type, null, "detail");
            lines.Add(line);
            previous = line.Hash;
        }
        return [.. lines];
    }

    private static BatchOutcome Done(TaskBatch batch) =>
        new(BatchState.Done, null, [.. batch.Tasks.Select(task => new TaskOutcome(task.Id, TaskResult.DryRun, "would run"))]);

    [Fact]
    public async Task A_batch_written_with_its_approvals_is_handed_out_once_with_its_code_and_the_code_is_gone()
    {
        var batch = await Queue();

        var claim = await _queue.ClaimAsync("executor-1", Ct);

        claim.Status.Should().Be(ClaimStatus.Claimed);
        claim.Delivery!.Batch.Digest().Should().Be(batch.Digest(), "what is read back is what was written");
        claim.Delivery.SecondFactorCode.Should().Be("123456");
        (await _queue.ClaimAsync("executor-2", Ct)).Status.Should().Be(ClaimStatus.Nothing);
        _approvals.List().Should().OnlyContain(approval => approval.State == ApprovalState.Taken && approval.BatchId == batch.Id);
        using var connection = _database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT code FROM batches;";
        (await command.ExecuteScalarAsync(Ct)).Should().Be(DBNull.Value, "the code leaves the file with the batch");
    }

    [Fact]
    public async Task The_oldest_batch_goes_first_and_the_stop_hands_nothing_out()
    {
        var first = await Queue(1);
        _time.Advance(TimeSpan.FromSeconds(3));
        await Queue(2);
        await _stop.ChangeAsync(true, new AuditEntry(Start, AuditEvents.KillSwitchEngaged, "alice", null, null), Ct);

        (await _queue.ClaimAsync("executor-1", Ct)).Status.Should().Be(ClaimStatus.Stopped);

        await _stop.ChangeAsync(false, new AuditEntry(Start, AuditEvents.KillSwitchReleased, "alice", null, null), Ct);
        (await _queue.ClaimAsync("executor-1", Ct)).Delivery!.Batch.Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task A_batch_past_its_time_expires_with_its_approvals_and_the_decision_may_be_given_again()
    {
        var batch = await Queue();
        _time.Advance(Options.BatchLifetime + TimeSpan.FromSeconds(1));

        (await _queue.ClaimAsync("executor-1", Ct)).Status.Should().Be(ClaimStatus.Nothing);

        _queue.Find(batch.Id)!.Summary.State.Should().Be(BatchState.Expired);
        _approvals.List().Should().OnlyContain(approval => approval.State == ApprovalState.Expired && approval.EndedAt != null);
        _audit.List().Should().Contain(entry => entry.Event == AuditEvents.BatchExpired);
        await Queue();
    }

    [Fact]
    public async Task An_expiry_pass_ends_the_batches_past_their_time_with_no_executor_and_while_the_stop_is_engaged()
    {
        var batch = await Queue();
        await _stop.ChangeAsync(true, new AuditEntry(Start, AuditEvents.KillSwitchEngaged, "alice", null, null), Ct);
        _time.Advance(Options.BatchLifetime + TimeSpan.FromSeconds(1));

        (await _queue.ExpireDueAsync(Ct)).Should().BeTrue();

        _queue.Find(batch.Id)!.Summary.State.Should().Be(BatchState.Expired);
        _approvals.List().Should().OnlyContain(approval => approval.State == ApprovalState.Expired && approval.EndedAt != null);
        await _stop.ChangeAsync(false, new AuditEntry(Start, AuditEvents.KillSwitchReleased, "alice", null, null), Ct);
        await Queue();
    }

    [Fact]
    public async Task Lines_that_are_sent_again_after_a_lost_answer_are_skipped_and_the_rest_is_taken()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("executor-1", Ct);
        var lines = Chain(batch, RunEvents.Claimed, RunEvents.SecondFactorChecked, RunEvents.RunCompleted);
        (await _queue.AppendEventsAsync(batch.Id, "executor-1", lines[..2], Ct)).Should().Be(QueueResult.Ok);

        (await _queue.AppendEventsAsync(batch.Id, "executor-1", lines, Ct)).Should().Be(QueueResult.Ok, "the first two are held, the third is new");

        _queue.Find(batch.Id)!.Events.Select(line => line.Sequence).Should().Equal(1, 2, 3);
        (await _queue.AppendEventsAsync(batch.Id, "executor-1", [lines[0] with { Detail = "changed" }], Ct)).Should().Be(QueueResult.Invalid, "a held line that differs is refused");
    }

    [Fact]
    public async Task A_run_that_keeps_sending_lines_is_not_expired_however_long_it_lasts()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("executor-1", Ct);
        var lines = Chain(batch, RunEvents.Claimed, RunEvents.SecondFactorChecked, RunEvents.RunCompleted);
        _time.Advance(Options.DeliveryTimeout - TimeSpan.FromMinutes(1));
        (await _queue.AppendEventsAsync(batch.Id, "executor-1", lines[..1], Ct)).Should().Be(QueueResult.Ok);
        _time.Advance(Options.DeliveryTimeout - TimeSpan.FromMinutes(1));
        (await _queue.AppendEventsAsync(batch.Id, "executor-1", lines[1..2], Ct)).Should().Be(QueueResult.Ok, "the line before renewed the delivery");

        await _queue.ClaimAsync("executor-1", Ct);

        _queue.Find(batch.Id)!.Summary.State.Should().Be(BatchState.Delivered);
    }

    [Fact]
    public async Task A_batch_handed_out_and_never_answered_ends_as_expired()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("executor-1", Ct);
        _time.Advance(Options.DeliveryTimeout + TimeSpan.FromSeconds(1));

        await _queue.ClaimAsync("executor-1", Ct);

        _queue.Find(batch.Id)!.Summary.State.Should().Be(BatchState.Expired);
    }

    [Fact]
    public async Task While_an_approval_is_open_the_same_decision_cannot_be_recorded_again()
    {
        await Queue();

        var again = () => Queue();

        await again.Should().ThrowAsync<Exception>("the same pull request at the same commit is already waiting");
        _queue.Runs(10).Should().ContainSingle("the second attempt left nothing behind");
    }

    [Fact]
    public async Task The_lines_of_a_run_are_kept_in_their_chain_and_a_broken_chain_is_refused()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("executor-1", Ct);
        var lines = Chain(batch, RunEvents.Claimed, RunEvents.SecondFactorChecked, RunEvents.RunCompleted);

        (await _queue.AppendEventsAsync(batch.Id, "executor-1", [lines[1]], Ct)).Should().Be(QueueResult.Invalid);
        (await _queue.AppendEventsAsync(batch.Id, "executor-1", lines[..2], Ct)).Should().Be(QueueResult.Ok);
        (await _queue.AppendEventsAsync(batch.Id, "executor-1", [lines[2] with { Detail = "changed" }], Ct)).Should().Be(QueueResult.Invalid);
        (await _queue.AppendEventsAsync(batch.Id, "executor-1", lines[2..], Ct)).Should().Be(QueueResult.Ok);

        _queue.Find(batch.Id)!.Events.Select(line => line.Type).Should().Equal(RunEvents.Claimed, RunEvents.SecondFactorChecked, RunEvents.RunCompleted);
    }

    [Fact]
    public async Task The_trace_of_a_run_cannot_be_changed_or_removed_afterwards()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("executor-1", Ct);
        await _queue.AppendEventsAsync(batch.Id, "executor-1", Chain(batch, RunEvents.Claimed), Ct);

        using var connection = _database.Connect();
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
    public async Task Only_the_executor_that_took_a_batch_may_write_to_it_or_end_it()
    {
        var batch = await Queue();
        (await _queue.AppendEventsAsync(batch.Id, "executor-1", [], Ct)).Should().Be(QueueResult.NotYours);
        await _queue.ClaimAsync("executor-1", Ct);

        (await _queue.AppendEventsAsync(batch.Id, "executor-2", Chain(batch, RunEvents.Claimed), Ct)).Should().Be(QueueResult.NotYours);
        (await _queue.CompleteAsync(batch.Id, "executor-2", Done(batch), Ct)).Should().Be(QueueResult.NotYours);
        (await _queue.CompleteAsync(Guid.NewGuid(), "executor-1", Done(batch), Ct)).Should().Be(QueueResult.NotFound);
    }

    [Fact]
    public async Task What_became_of_the_tasks_that_ended_is_listed_newest_first_with_the_whole_row_and_a_running_batch_has_none()
    {
        var first = await Queue();
        await _queue.ClaimAsync("executor-1", Ct);
        _queue.TaskResults(10).Should().BeEmpty("nothing has ended");
        await _queue.CompleteAsync(first.Id, "executor-1", Outcome(first, TaskResult.Failed, "npm test failed (exit 1)"), Ct);
        _time.Advance(TimeSpan.FromMinutes(5));
        var second = await Queue();
        await _queue.ClaimAsync("executor-1", Ct);
        await _queue.CompleteAsync(second.Id, "executor-1", Outcome(second, TaskResult.Passed, "the validation passes"), Ct);

        var rows = _queue.TaskResults(10);
        var lastTask = second.Tasks[^1];

        rows.Should().HaveCount(4);
        rows.Select(row => row.Result).Distinct().Should().Equal(TaskResult.Passed, TaskResult.Failed);
        rows[0].Should().Match<TaskResultRow>(row => row.Reason == "the validation passes" && row.At == Start.AddMinutes(5) && row.Repository == lastTask.Repository && row.HeadSha == lastTask.HeadSha && row.Kind == lastTask.Kind, "the last task of the newest batch is the first row");
        rows[^1].At.Should().Be(Start);
        _queue.TaskResults(1).Should().HaveCount(1);
    }

    private static BatchOutcome Outcome(TaskBatch batch, TaskResult result, string reason) =>
        new(BatchState.Done, null, [.. batch.Tasks.Select(task => new TaskOutcome(task.Id, result, reason))]);

    [Fact]
    public async Task A_batch_that_ended_done_spends_its_approvals_keeps_each_outcome_and_may_be_approved_again()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("executor-1", Ct);

        (await _queue.CompleteAsync(batch.Id, "executor-1", Done(batch), Ct)).Should().Be(QueueResult.Ok);

        var run = _queue.Find(batch.Id)!;
        run.Summary.Should().Match<RunSummary>(summary => summary.State == BatchState.Done && summary.EndedAt == Start && summary.Executor == "executor-1" && summary.TaskCount == 2);
        run.Tasks.Should().OnlyContain(task => task.Result == TaskResult.DryRun && task.Reason == "would run");
        _approvals.List().Should().OnlyContain(approval => approval.State == ApprovalState.DryRunDone);
        _audit.List().Should().Contain(entry => entry.Event == AuditEvents.BatchDone && entry.Subject == "executor-1");
        (await _queue.CompleteAsync(batch.Id, "executor-1", Done(batch), Ct)).Should().Be(QueueResult.NotYours, "a batch ends once");
        await Queue();
    }

    [Fact]
    public async Task A_refused_batch_ends_with_its_code()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("executor-1", Ct);

        (await _queue.CompleteAsync(batch.Id, "executor-1", new BatchOutcome(BatchState.Refused, RunRefusal.SecondFactor, []), Ct)).Should().Be(QueueResult.Ok);

        _queue.Find(batch.Id)!.Summary.Should().Match<RunSummary>(summary => summary.State == BatchState.Refused && summary.Refusal == RunRefusal.SecondFactor);
        _approvals.List().Should().OnlyContain(approval => approval.State == ApprovalState.Refused);
    }

    [Fact]
    public async Task An_outcome_for_a_task_that_is_not_in_the_batch_is_refused_and_changes_nothing()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("executor-1", Ct);

        (await _queue.CompleteAsync(batch.Id, "executor-1", new BatchOutcome(BatchState.Done, null, [new TaskOutcome(Guid.NewGuid(), TaskResult.DryRun, null)]), Ct)).Should().Be(QueueResult.Invalid);

        _queue.Find(batch.Id)!.Summary.State.Should().Be(BatchState.Delivered);
    }

    [Fact]
    public async Task What_is_kept_survives_a_restart()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("executor-1", Ct);
        await _queue.AppendEventsAsync(batch.Id, "executor-1", Chain(batch, RunEvents.Claimed), Ct);
        await _queue.CompleteAsync(batch.Id, "executor-1", Done(batch), Ct);

        var restarted = new SqliteBatchQueue(_file.Open(), _stop, _time, Options);

        restarted.Runs(10).Should().ContainSingle().Which.State.Should().Be(BatchState.Done);
        restarted.Find(batch.Id)!.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task A_queue_whose_trail_has_no_room_hands_nothing_out()
    {
        await Queue();
        var tiny = new SqliteBatchQueue(_database, _stop, _time, Options, auditCapacity: 1);

        (await tiny.ClaimAsync("executor-1", Ct)).Status.Should().Be(ClaimStatus.Unavailable);

        _queue.Runs(1).Single().State.Should().Be(BatchState.Waiting, "a batch that was not traced was not handed out");
    }

    [Fact]
    public async Task A_file_that_holds_approvals_of_version_one_is_migrated_and_they_end_as_expired()
    {
        // Rebuild what version 1 wrote: the old table with its table-level key, no batch, no state
        using (var connection = _database.Connect())
        {
            SqliteDatabase.Run(connection, """
                DROP INDEX approvals_open_decision;
                DROP INDEX approvals_by_batch;
                ALTER TABLE approvals RENAME TO approvals_new;
                CREATE TABLE approvals (
                  id TEXT NOT NULL PRIMARY KEY, work_item_id TEXT NOT NULL, repository TEXT NOT NULL, pull_request INTEGER NOT NULL, head_sha TEXT NOT NULL,
                  steps TEXT NOT NULL, approver TEXT NOT NULL, approved_at TEXT NOT NULL, step_up_at TEXT NULL,
                  UNIQUE (repository, pull_request, head_sha, steps));
                DROP TABLE approvals_new;
                DROP TABLE run_events;
                DROP TABLE tasks;
                DROP TABLE batches;
                INSERT INTO approvals VALUES ('11111111-1111-1111-1111-111111111111', 'repository:example/app', 'example/app', 9, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'Validate', 'alice', '2026-11-03T12:00:00.0000000Z', NULL);
                PRAGMA user_version = 1;
                """);
        }

        var migrated = SqliteDatabase.Open(_file.Path);

        var approval = new SqliteApprovalStore(migrated).List().Should().ContainSingle().Subject;
        approval.State.Should().Be(ApprovalState.Expired, "no executor can take an approval that has no batch");
        approval.EndedAt.Should().NotBeNull();
        approval.BatchId.Should().BeNull();
        approval.PullRequest.Should().Be(9);
        new SqliteBatchQueue(migrated, _stop, _time, Options).Runs(10).Should().BeEmpty("the new tables are there");
    }
}
