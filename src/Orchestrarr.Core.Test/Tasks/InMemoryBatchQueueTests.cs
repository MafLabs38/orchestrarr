using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Test.Tasks;

public sealed class InMemoryBatchQueueTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Head = new('a', 40);
    private static readonly TaskOptions Options = new();

    private readonly FakeTimeProvider _time = new(Start);
    private readonly InMemoryApprovalStore _store = new();
    private readonly InMemoryAuditLog _audit = new();
    private readonly InMemoryKillSwitch _stop = new();
    private readonly InMemoryBatchQueue _queue;
    private readonly InMemoryApprovalLedger _ledger;

    public InMemoryBatchQueueTests()
    {
        _queue = new InMemoryBatchQueue(_store, _audit, _stop, _time, Options);
        _ledger = new InMemoryApprovalLedger(_store, _audit, _stop, _queue);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<TaskBatch> Queue(int pullRequest = 1, string code = "123456")
    {
        var approval = new Approval(Guid.NewGuid(), "repository:example/app", "example/app", pullRequest, Head, [StepKind.Validate, StepKind.MergePullRequests], "alice", _time.GetUtcNow(), null, ApprovalState.Waiting, null);
        var batchId = Guid.NewGuid();
        approval = approval with { BatchId = batchId };
        var batch = new TaskBatch(
            batchId, approval.WorkItemId, "alice", _time.GetUtcNow(), _time.GetUtcNow() + Options.BatchLifetime,
            [
                new TaskSpec(Guid.NewGuid(), 1, TaskKind.ValidatePullRequest, "example/app", pullRequest, Head, approval.Id),
                new TaskSpec(Guid.NewGuid(), 2, TaskKind.MergePullRequest, "example/app", pullRequest, Head, approval.Id),
            ]);
        (await _ledger.TryRecordAsync([approval], batch, code, [], Ct)).Should().Be(LedgerResult.Recorded);
        return batch;
    }

    private static RunEvent[] Chain(TaskBatch batch, params string[] types)
    {
        var previous = batch.Digest();
        var lines = new List<RunEvent>();
        foreach (var type in types)
        {
            var line = RunEvent.Create(previous, lines.Count + 1, Start, type, null, null);
            lines.Add(line);
            previous = line.Hash;
        }
        return [.. lines];
    }

    private static BatchOutcome Done(TaskBatch batch) =>
        new(BatchState.Done, null, [.. batch.Tasks.Select(task => new TaskOutcome(task.Id, TaskResult.DryRun, null))]);

    [Fact]
    public async Task Claims_made_at_once_hand_the_batch_to_one_runner_only()
    {
        await Queue();

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => _queue.ClaimAsync($"runner-{index}", Ct).AsTask()));

        claims.Count(claim => claim.Status == ClaimStatus.Claimed).Should().Be(1);
        claims.Where(claim => claim.Status != ClaimStatus.Claimed).Should().OnlyContain(claim => claim.Status == ClaimStatus.Nothing);
    }

    [Fact]
    public async Task A_batch_whose_hand_over_cannot_be_traced_waits_again_with_its_code()
    {
        var audit = Substitute.For<IAuditSink>();
        audit.TryRecordAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(false), new ValueTask<bool>(true));
        var queue = new InMemoryBatchQueue(_store, audit, _stop, _time, Options);
        var ledger = new InMemoryApprovalLedger(_store, _audit, _stop, queue);
        var approval = new Approval(Guid.NewGuid(), "repository:example/app", "example/app", 1, Head, [StepKind.Validate], "alice", _time.GetUtcNow(), null, ApprovalState.Waiting, null);
        var batchId = Guid.NewGuid();
        var batch = new TaskBatch(batchId, approval.WorkItemId, "alice", _time.GetUtcNow(), _time.GetUtcNow() + Options.BatchLifetime, [new TaskSpec(Guid.NewGuid(), 1, TaskKind.ValidatePullRequest, "example/app", 1, Head, approval.Id)]);
        (await ledger.TryRecordAsync([approval with { BatchId = batchId }], batch, "654321", [], Ct)).Should().Be(LedgerResult.Recorded);

        (await queue.ClaimAsync("runner-1", Ct)).Status.Should().Be(ClaimStatus.Unavailable);
        var second = await queue.ClaimAsync("runner-1", Ct);

        second.Status.Should().Be(ClaimStatus.Claimed);
        second.Delivery!.SecondFactorCode.Should().Be("654321");
    }

    [Fact]
    public async Task A_batch_is_handed_to_one_runner_once_with_its_code()
    {
        var batch = await Queue();

        var claim = await _queue.ClaimAsync("runner-1", Ct);

        claim.Status.Should().Be(ClaimStatus.Claimed);
        claim.Delivery!.Batch.Should().BeEquivalentTo(batch);
        claim.Delivery.SecondFactorCode.Should().Be("123456");
        (await _queue.ClaimAsync("runner-2", Ct)).Status.Should().Be(ClaimStatus.Nothing, "a batch is handed out once");
        _store.List().Should().OnlyContain(approval => approval.State == ApprovalState.Taken);
        _audit.List().Should().Contain(entry => entry.Event == AuditEvents.BatchDelivered && entry.Subject == "runner-1" && entry.OrderId == batch.Id.ToString("D"));
    }

    [Fact]
    public async Task The_oldest_batch_goes_first()
    {
        var first = await Queue(1);
        _time.Advance(TimeSpan.FromSeconds(5));
        await Queue(2);

        (await _queue.ClaimAsync("runner-1", Ct)).Delivery!.Batch.Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task Nothing_is_handed_out_while_the_stop_is_engaged_and_the_batch_waits()
    {
        await Queue();
        await _stop.ChangeAsync(true, new AuditEntry(Start, AuditEvents.KillSwitchEngaged, "alice", null, null), Ct);

        (await _queue.ClaimAsync("runner-1", Ct)).Status.Should().Be(ClaimStatus.Stopped);

        await _stop.ChangeAsync(false, new AuditEntry(Start, AuditEvents.KillSwitchReleased, "alice", null, null), Ct);
        (await _queue.ClaimAsync("runner-1", Ct)).Status.Should().Be(ClaimStatus.Claimed);
    }

    [Fact]
    public async Task A_batch_that_waited_too_long_expires_with_its_approvals_and_may_be_approved_again()
    {
        var batch = await Queue();
        _time.Advance(Options.BatchLifetime + TimeSpan.FromSeconds(1));

        (await _queue.ClaimAsync("runner-1", Ct)).Status.Should().Be(ClaimStatus.Nothing);

        _queue.Find(batch.Id)!.Summary.State.Should().Be(BatchState.Expired);
        _store.List().Should().OnlyContain(approval => approval.State == ApprovalState.Expired && approval.EndedAt != null);
        _audit.List().Should().Contain(entry => entry.Event == AuditEvents.BatchExpired);
        await Queue();
    }

    [Fact]
    public async Task An_expiry_pass_ends_the_batches_past_their_time_with_no_runner_and_while_the_stop_is_engaged()
    {
        var batch = await Queue();
        await _stop.ChangeAsync(true, new AuditEntry(Start, AuditEvents.KillSwitchEngaged, "alice", null, null), Ct);
        _time.Advance(Options.BatchLifetime + TimeSpan.FromSeconds(1));

        (await _queue.ExpireDueAsync(Ct)).Should().BeTrue();

        _queue.Find(batch.Id)!.Summary.State.Should().Be(BatchState.Expired);
        _store.List().Should().OnlyContain(approval => approval.State == ApprovalState.Expired && approval.EndedAt != null);
        await _stop.ChangeAsync(false, new AuditEntry(Start, AuditEvents.KillSwitchReleased, "alice", null, null), Ct);
        await Queue();
    }

    [Fact]
    public async Task Lines_that_are_sent_again_after_a_lost_answer_are_skipped_and_the_rest_is_taken()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("runner-1", Ct);
        var lines = Chain(batch, RunEvents.Claimed, RunEvents.SecondFactorChecked, RunEvents.RunCompleted);
        (await _queue.AppendEventsAsync(batch.Id, "runner-1", lines[..2], Ct)).Should().Be(QueueResult.Ok);

        (await _queue.AppendEventsAsync(batch.Id, "runner-1", lines, Ct)).Should().Be(QueueResult.Ok, "the first two are held, the third is new");

        _queue.Find(batch.Id)!.Events.Select(line => line.Sequence).Should().Equal(1, 2, 3);
        (await _queue.AppendEventsAsync(batch.Id, "runner-1", [lines[0] with { Detail = "changed" }], Ct)).Should().Be(QueueResult.Invalid, "a held line that differs is refused");
    }

    [Fact]
    public async Task A_send_that_is_all_held_is_taken_and_a_gap_or_an_empty_send_behaves()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("runner-1", Ct);
        var lines = Chain(batch, RunEvents.Claimed, RunEvents.SecondFactorChecked, RunEvents.RunCompleted);
        await _queue.AppendEventsAsync(batch.Id, "runner-1", lines[..2], Ct);

        (await _queue.AppendEventsAsync(batch.Id, "runner-1", lines[..2], Ct)).Should().Be(QueueResult.Ok, "all held: nothing added");
        (await _queue.AppendEventsAsync(batch.Id, "runner-1", [], Ct)).Should().Be(QueueResult.Ok, "an empty send says the runner is alive");
        (await _queue.AppendEventsAsync(batch.Id, "runner-2", [], Ct)).Should().Be(QueueResult.NotYours);
        _queue.Find(batch.Id)!.Events.Should().HaveCount(2);
        var gap = Chain(batch, RunEvents.Claimed, RunEvents.SecondFactorChecked, RunEvents.BatchChecked, RunEvents.RunCompleted);
        (await _queue.AppendEventsAsync(batch.Id, "runner-1", [gap[3]], Ct)).Should().Be(QueueResult.Invalid, "sequence 4 when two are held");
    }

    [Fact]
    public async Task A_refused_send_does_not_renew_the_delivery_and_an_empty_send_does()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("runner-1", Ct);
        var lines = Chain(batch, RunEvents.Claimed, RunEvents.SecondFactorChecked);
        _time.Advance(Options.DeliveryTimeout - TimeSpan.FromMinutes(1));
        (await _queue.AppendEventsAsync(batch.Id, "runner-1", [lines[1]], Ct)).Should().Be(QueueResult.Invalid);
        _time.Advance(TimeSpan.FromMinutes(2));

        await _queue.ClaimAsync("runner-1", Ct);

        _queue.Find(batch.Id)!.Summary.State.Should().Be(BatchState.Expired, "junk does not keep a batch alive");
    }

    [Fact]
    public async Task A_run_that_keeps_sending_lines_is_not_expired_however_long_it_lasts()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("runner-1", Ct);
        var lines = Chain(batch, RunEvents.Claimed, RunEvents.SecondFactorChecked, RunEvents.RunCompleted);
        _time.Advance(Options.DeliveryTimeout - TimeSpan.FromMinutes(1));
        (await _queue.AppendEventsAsync(batch.Id, "runner-1", lines[..1], Ct)).Should().Be(QueueResult.Ok);
        _time.Advance(Options.DeliveryTimeout - TimeSpan.FromMinutes(1));
        (await _queue.AppendEventsAsync(batch.Id, "runner-1", lines[1..2], Ct)).Should().Be(QueueResult.Ok, "the line before renewed the delivery");

        await _queue.ClaimAsync("runner-1", Ct);

        _queue.Find(batch.Id)!.Summary.State.Should().Be(BatchState.Delivered);
    }

    [Fact]
    public async Task A_batch_that_an_runner_never_answered_ends_as_expired()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("runner-1", Ct);
        _time.Advance(Options.DeliveryTimeout + TimeSpan.FromSeconds(1));

        await _queue.ClaimAsync("runner-1", Ct);

        _queue.Find(batch.Id)!.Summary.State.Should().Be(BatchState.Expired);
        (await _queue.CompleteAsync(batch.Id, "runner-1", Done(batch), Ct)).Should().Be(QueueResult.NotYours, "a late answer to an expired batch is refused");
    }

    [Fact]
    public async Task The_lines_of_a_run_are_kept_when_they_continue_the_chain_from_the_digest()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("runner-1", Ct);
        var lines = Chain(batch, RunEvents.Claimed, RunEvents.SecondFactorChecked, RunEvents.RunCompleted);

        (await _queue.AppendEventsAsync(batch.Id, "runner-1", lines[..2], Ct)).Should().Be(QueueResult.Ok);
        (await _queue.AppendEventsAsync(batch.Id, "runner-1", lines[2..], Ct)).Should().Be(QueueResult.Ok);

        _queue.Find(batch.Id)!.Events.Select(line => line.Type).Should().Equal(RunEvents.Claimed, RunEvents.SecondFactorChecked, RunEvents.RunCompleted);
    }

    [Fact]
    public async Task Lines_that_do_not_continue_the_chain_are_refused_and_not_kept()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("runner-1", Ct);
        var lines = Chain(batch, RunEvents.Claimed, RunEvents.RunCompleted);

        (await _queue.AppendEventsAsync(batch.Id, "runner-1", [lines[1]], Ct)).Should().Be(QueueResult.Invalid);
        (await _queue.AppendEventsAsync(batch.Id, "runner-1", [lines[0] with { Detail = "changed" }], Ct)).Should().Be(QueueResult.Invalid);
        _queue.Find(batch.Id)!.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_cannot_hold_more_lines_or_longer_lines_than_the_limit()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("runner-1", Ct);
        var tooLong = RunEvent.Create(batch.Digest(), 1, Start, RunEvents.Claimed, null, new string('x', Options.MaxEventDetail + 1));

        (await _queue.AppendEventsAsync(batch.Id, "runner-1", [tooLong], Ct)).Should().Be(QueueResult.Invalid);
        (await _queue.AppendEventsAsync(batch.Id, "runner-1", Chain(batch, [.. Enumerable.Repeat(RunEvents.Claimed, Options.MaxEventsPerRun + 1)]), Ct)).Should().Be(QueueResult.Invalid);
    }

    [Fact]
    public async Task Only_the_runner_that_took_a_batch_may_write_to_it_or_end_it()
    {
        var batch = await Queue();
        (await _queue.AppendEventsAsync(batch.Id, "runner-1", [], Ct)).Should().Be(QueueResult.NotYours, "a batch that was not handed out has no run yet");
        await _queue.ClaimAsync("runner-1", Ct);

        (await _queue.AppendEventsAsync(batch.Id, "runner-2", Chain(batch, RunEvents.Claimed), Ct)).Should().Be(QueueResult.NotYours);
        (await _queue.CompleteAsync(batch.Id, "runner-2", Done(batch), Ct)).Should().Be(QueueResult.NotYours);
        (await _queue.CompleteAsync(Guid.NewGuid(), "runner-1", Done(batch), Ct)).Should().Be(QueueResult.NotFound);
    }

    [Fact]
    public async Task What_became_of_the_tasks_that_ended_is_listed_newest_first_with_the_whole_row_and_a_running_batch_has_none()
    {
        var first = await Queue();
        await _queue.ClaimAsync("runner-1", Ct);
        _queue.TaskResults(10).Should().BeEmpty("nothing has ended");
        await _queue.CompleteAsync(first.Id, "runner-1", Outcome(first, TaskResult.Failed, "npm test failed (exit 1)"), Ct);
        _time.Advance(TimeSpan.FromMinutes(5));
        var second = await Queue();
        await _queue.ClaimAsync("runner-1", Ct);
        await _queue.CompleteAsync(second.Id, "runner-1", Outcome(second, TaskResult.Passed, "the validation passes"), Ct);

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
    public async Task A_batch_that_ended_done_spends_its_approvals_and_the_operator_may_approve_again()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("runner-1", Ct);

        (await _queue.CompleteAsync(batch.Id, "runner-1", Done(batch), Ct)).Should().Be(QueueResult.Ok);

        var run = _queue.Find(batch.Id)!;
        run.Summary.State.Should().Be(BatchState.Done);
        run.Summary.EndedAt.Should().Be(Start);
        run.Tasks.Should().OnlyContain(task => task.Result == TaskResult.DryRun);
        _store.List().Should().OnlyContain(approval => approval.State == ApprovalState.DryRunDone);
        _audit.List().Should().Contain(entry => entry.Event == AuditEvents.BatchDone);
        await Queue();
        (await _queue.CompleteAsync(batch.Id, "runner-1", Done(batch), Ct)).Should().Be(QueueResult.NotYours, "a batch ends once");
    }

    [Fact]
    public async Task A_refused_batch_ends_with_its_code_and_its_approvals_are_refused()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("runner-1", Ct);

        (await _queue.CompleteAsync(batch.Id, "runner-1", new BatchOutcome(BatchState.Refused, RunRefusal.SecondFactor, []), Ct)).Should().Be(QueueResult.Ok);

        _queue.Find(batch.Id)!.Summary.Should().Match<RunSummary>(summary => summary.State == BatchState.Refused && summary.Refusal == RunRefusal.SecondFactor);
        _store.List().Should().OnlyContain(approval => approval.State == ApprovalState.Refused);
        _audit.List().Should().Contain(entry => entry.Event == AuditEvents.BatchRefused && entry.Detail == nameof(RunRefusal.SecondFactor));
    }

    [Fact]
    public async Task An_outcome_for_a_task_that_is_not_in_the_batch_or_with_a_state_that_is_not_an_end_is_refused()
    {
        var batch = await Queue();
        await _queue.ClaimAsync("runner-1", Ct);

        (await _queue.CompleteAsync(batch.Id, "runner-1", new BatchOutcome(BatchState.Done, null, [new TaskOutcome(Guid.NewGuid(), TaskResult.DryRun, null)]), Ct)).Should().Be(QueueResult.Invalid);
        (await _queue.CompleteAsync(batch.Id, "runner-1", new BatchOutcome(BatchState.Waiting, null, []), Ct)).Should().Be(QueueResult.Invalid);
    }

    [Theory]
    [InlineData(TaskKind.ValidatePullRequest, TaskResult.Repaired, false)]
    [InlineData(TaskKind.MergePullRequest, TaskResult.Repaired, false)]
    [InlineData(TaskKind.RepairPullRequest, TaskResult.DryRun, false)]
    [InlineData(TaskKind.ValidatePullRequest, TaskResult.Passed, true)]
    [InlineData(TaskKind.RepairPullRequest, TaskResult.Repaired, true)]
    [InlineData(TaskKind.MergePullRequest, TaskResult.DryRun, true)]
    public void A_result_that_does_not_fit_the_kind_of_the_task_is_not_accepted(TaskKind kind, TaskResult result, bool fits)
    {
        TaskResultRules.Fits(kind, result).Should().Be(fits);
    }

    [Fact]
    public async Task A_decision_that_is_still_open_cannot_be_approved_twice()
    {
        await Queue();

        var again = () => Queue();

        await again.Should().ThrowAsync<Exception>("the same pull request at the same commit is already waiting");
    }

    [Fact]
    public async Task The_runs_come_newest_first_with_their_tasks()
    {
        await Queue(1);
        _time.Advance(TimeSpan.FromSeconds(5));
        var newer = await Queue(2);

        var runs = _queue.Runs(10);

        runs.Select(run => run.BatchId).First().Should().Be(newer.Id);
        _queue.Runs(1).Should().ContainSingle();
        _queue.Find(newer.Id)!.Tasks.Select(task => task.Spec.Kind).Should().Equal(TaskKind.ValidatePullRequest, TaskKind.MergePullRequest);
        _queue.Find(Guid.NewGuid()).Should().BeNull();
    }
}
