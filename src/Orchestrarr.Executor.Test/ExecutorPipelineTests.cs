using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor.Test;

public sealed class ExecutorPipelineTests : IDisposable
{
    private readonly ExecutorFixture _world = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _world.Dispose();

    private async Task<PipelineResult> Run(TaskBatch batch, string? code, bool stopped = false, ExecutorPipeline? pipeline = null) =>
        await (pipeline ?? _world.Pipeline()).RunAsync(new BatchDelivery(batch, code), () => stopped, Ct);

    private static string[] Types(PipelineResult result) => [.. result.Events.Select(line => line.Type)];

    [Fact]
    public async Task A_batch_that_was_approved_within_the_rights_with_the_right_code_is_dry_run_task_by_task()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch());

        var result = await Run(batch, _world.CodeAt());

        result.Outcome.Final.Should().Be(BatchState.Done);
        result.Outcome.Refusal.Should().BeNull();
        result.Outcome.Tasks.Should().HaveCount(2).And.OnlyContain(task => task.Result == TaskResult.DryRun);
        Types(result).Should().Equal(RunEvents.Claimed, RunEvents.BatchChecked, RunEvents.DoubleControl, RunEvents.RightsChecked, RunEvents.SecondFactorChecked, RunEvents.TaskDryRun, RunEvents.TaskDryRun, RunEvents.RunCompleted);
        result.Outcome.Tasks[0].Reason.Should().StartWith("would validate").And.Contain("example/app#1");
        result.Outcome.Tasks[^1].Reason.Should().StartWith("would merge").And.Contain("checks green");
    }

    [Fact]
    public async Task The_trace_is_one_chain_from_the_digest_of_the_batch_and_is_kept_in_the_executors_own_file()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch());

        var result = await Run(batch, _world.CodeAt());

        RunEvent.Continues(batch.Digest(), 0, result.Events).Should().BeTrue();
        _world.Store.Trace(batch.Id).Should().BeEquivalentTo(result.Events, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task The_trace_never_holds_the_code()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch());
        var code = _world.CodeAt();

        var result = await Run(batch, code);

        result.Events.Should().OnlyContain(line => line.Detail == null || !line.Detail.Contains(code, StringComparison.Ordinal));
    }

    [Fact]
    public async Task While_the_stop_is_engaged_nothing_is_checked_or_run()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch());

        var result = await Run(batch, _world.CodeAt(), stopped: true);

        result.Outcome.Should().Match<BatchOutcome>(outcome => outcome.Final == BatchState.Refused && outcome.Refusal == RunRefusal.Stopped);
        Types(result).Should().Equal(RunEvents.Claimed, RunEvents.Stopped, RunEvents.RunRefused);
        (await Run(batch, _world.CodeAt())).Outcome.Final.Should().Be(BatchState.Done, "a stopped batch burned nothing: it was refused before it was registered");
    }

    [Theory]
    [InlineData("example/app", "not-a-commit")]
    [InlineData("not a repository", null)]
    [InlineData("../etc/passwd", null)]
    public async Task A_batch_whose_fields_do_not_have_the_shape_of_real_ones_is_malformed(string repository, string? head)
    {
        var batch = ExecutorFixture.Batch(repository) with { };
        if (head is not null)
        {
            batch = batch with { Tasks = [.. batch.Tasks.Select(task => task with { HeadSha = head })] };
        }

        (await Run(batch, _world.CodeAt())).Outcome.Refusal.Should().Be(RunRefusal.Malformed);
    }

    [Fact]
    public async Task A_batch_with_no_task_with_gaps_in_its_order_or_a_repeated_task_is_malformed()
    {
        var one = ExecutorFixture.Batch();

        (await Run(one with { Tasks = [] }, _world.CodeAt())).Outcome.Refusal.Should().Be(RunRefusal.Malformed);
        (await Run(one with { Tasks = [one.Tasks[0] with { Sequence = 5 }] }, _world.CodeAt())).Outcome.Refusal.Should().Be(RunRefusal.Malformed);
        (await Run(one with { Tasks = [one.Tasks[0], one.Tasks[0] with { Sequence = 2 }] }, _world.CodeAt())).Outcome.Refusal.Should().Be(RunRefusal.Malformed);
    }

    [Fact]
    public async Task A_batch_past_its_time_is_expired()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch(expires: ExecutorFixture.Start.AddMinutes(5)));
        _world.Time.Advance(TimeSpan.FromMinutes(6));

        (await Run(batch, _world.CodeAt())).Outcome.Refusal.Should().Be(RunRefusal.Expired);
    }

    [Fact]
    public async Task A_batch_that_the_journal_does_not_hold_is_refused_even_with_the_right_code()
    {
        var rogue = ExecutorFixture.Batch();

        var result = await Run(rogue, _world.CodeAt());

        result.Outcome.Refusal.Should().Be(RunRefusal.JournalMismatch);
        result.Events.Should().Contain(line => line.Type == RunEvents.DoubleControl && line.Detail!.Contains("NotFound", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_batch_changed_after_it_was_approved_does_not_match_its_journal_line()
    {
        var approved = await _world.Approved(ExecutorFixture.Batch());
        var tampered = approved with { Tasks = [.. approved.Tasks.Select(task => task with { PullRequest = 99 })] };

        var result = await Run(tampered, _world.CodeAt());

        result.Outcome.Refusal.Should().Be(RunRefusal.JournalMismatch);
        result.Events.Should().Contain(line => line.Detail != null && line.Detail.Contains("Mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_journal_that_was_altered_refuses_every_batch()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch());
        var file = Path.Combine(_world.Folder, "journal.jsonl");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file, Ct)).Replace("alice", "mallory", StringComparison.Ordinal), Ct);

        (await Run(batch, _world.CodeAt())).Outcome.Refusal.Should().Be(RunRefusal.JournalMismatch);
    }

    [Fact]
    public async Task A_kind_that_this_executor_does_not_accept_is_refused_whatever_the_task_says()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch());
        var rights = new RightsOptions { Kinds = [TaskKind.ValidatePullRequest], Repositories = ["example/*"] };

        (await Run(batch, _world.CodeAt(), pipeline: _world.Pipeline(rights: rights))).Outcome.Refusal.Should().Be(RunRefusal.KindNotAllowed);
    }

    [Fact]
    public async Task A_repository_outside_the_rights_is_refused()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch("other/app"));

        (await Run(batch, _world.CodeAt())).Outcome.Refusal.Should().Be(RunRefusal.RepositoryNotAllowed);
    }

    [Fact]
    public async Task With_no_rights_configured_nothing_is_accepted()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch());

        (await Run(batch, _world.CodeAt(), pipeline: _world.Pipeline(rights: new RightsOptions()))).Outcome.Refusal.Should().Be(RunRefusal.KindNotAllowed);
    }

    [Fact]
    public async Task A_batch_is_never_run_twice_even_with_a_fresh_code()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch());
        (await Run(batch, _world.CodeAt())).Outcome.Final.Should().Be(BatchState.Done);
        _world.Time.Advance(TimeSpan.FromSeconds(30));

        var second = await Run(batch, _world.CodeAt());

        second.Outcome.Refusal.Should().Be(RunRefusal.Replayed);
    }

    [Fact]
    public async Task A_batch_whose_code_is_wrong_is_refused_and_is_burned()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch());
        var right = _world.CodeAt();
        var wrong = right == "000000" ? "000001" : "000000";

        (await Run(batch, wrong)).Outcome.Refusal.Should().Be(RunRefusal.SecondFactor);
        (await Run(batch, right)).Outcome.Refusal.Should().Be(RunRefusal.Replayed, "a batch that failed its check is not tried again: the operator approves again");
    }

    [Fact]
    public async Task A_missing_code_or_an_operator_without_a_secret_is_refused()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch());
        var stranger = await _world.Approved(ExecutorFixture.Batch(subject: "mallory"));

        (await Run(batch, null)).Outcome.Refusal.Should().Be(RunRefusal.SecondFactor);
        (await Run(stranger, _world.CodeAt())).Outcome.Refusal.Should().Be(RunRefusal.SecondFactor);
    }

    [Fact]
    public async Task A_code_used_for_one_batch_is_not_good_for_another()
    {
        var first = await _world.Approved(ExecutorFixture.Batch(pullRequest: 1));
        var second = await _world.Approved(ExecutorFixture.Batch(pullRequest: 2));
        var code = _world.CodeAt();

        (await Run(first, code)).Outcome.Final.Should().Be(BatchState.Done);
        var other = await Run(second, code);

        other.Outcome.Refusal.Should().Be(RunRefusal.SecondFactor);
        other.Events.Should().Contain(line => line.Detail == "refused: Reused");
    }

    [Fact]
    public async Task A_kind_with_no_runner_is_refused_task_by_task_and_the_rest_still_runs()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch());

        var result = await Run(batch, _world.CodeAt(), pipeline: _world.Pipeline(runners: [new DryRunTaskRunner(TaskKind.ValidatePullRequest)]));

        result.Outcome.Final.Should().Be(BatchState.Done);
        result.Outcome.Tasks.Select(task => task.Result).Should().Equal(TaskResult.DryRun, TaskResult.Refused);
        Types(result).Should().Contain(RunEvents.TaskRefused);
    }

    // --- standing delegations

    private Delegation Delegate(TaskKind kind = TaskKind.MergePullRequest, string repository = "example/*", int quota = 5, int days = 30, DateTimeOffset? revokedAt = null)
    {
        var delegation = new Delegation(Guid.NewGuid(), "alice", kind, repository, ExecutorFixture.Start.AddDays(days), quota, ExecutorFixture.Start, null);
        _world.Store.AddDelegation(delegation);
        if (revokedAt is { } at)
        {
            _world.Store.RevokeDelegation(delegation.Id, at);
        }
        return delegation;
    }

    private TaskBatch Delegated(Delegation delegation, string repository = "example/app", TaskKind[]? kinds = null) =>
        ExecutorFixture.Batch(repository, $"delegation:{delegation.Id:D}", delegation.Id, expires: _world.Time.GetUtcNow().AddHours(1), kinds: kinds ?? [delegation.Kind]);

    [Fact]
    public async Task A_batch_under_a_standing_delegation_needs_no_code_and_uses_its_quota()
    {
        var delegation = Delegate(quota: 2);
        var first = await _world.Approved(Delegated(delegation));

        var result = await Run(first, null);

        result.Outcome.Final.Should().Be(BatchState.Done);
        result.Events.Should().Contain(line => line.Type == RunEvents.DelegationChecked && line.Detail!.Contains(delegation.Id.ToString("D"), StringComparison.Ordinal));
        Types(result).Should().NotContain(RunEvents.SecondFactorChecked);
        var second = await _world.Approved(Delegated(delegation));
        (await Run(second, null)).Outcome.Final.Should().Be(BatchState.Done);
        var third = await _world.Approved(Delegated(delegation));
        (await Run(third, null)).Outcome.Refusal.Should().Be(RunRefusal.DelegationInvalid, "the quota of two tasks a day is spent");
    }

    [Fact]
    public async Task The_quota_starts_again_the_next_day()
    {
        var delegation = Delegate(quota: 1);
        (await Run(await _world.Approved(Delegated(delegation)), null)).Outcome.Final.Should().Be(BatchState.Done);
        _world.Time.Advance(TimeSpan.FromHours(13));

        (await Run(await _world.Approved(Delegated(delegation)), null)).Outcome.Final.Should().Be(BatchState.Done);
    }

    [Fact]
    public async Task A_delegation_that_is_revoked_expired_or_unknown_covers_nothing()
    {
        var revoked = Delegate(revokedAt: ExecutorFixture.Start);
        var expired = Delegate(days: 1);
        _world.Time.Advance(TimeSpan.FromDays(2));
        var unknown = new Delegation(Guid.NewGuid(), "alice", TaskKind.MergePullRequest, "example/*", ExecutorFixture.Start.AddDays(9), 5, ExecutorFixture.Start, null);

        foreach (var delegation in new[] { revoked, expired, unknown })
        {
            (await Run(await _world.Approved(Delegated(delegation)), null)).Outcome.Refusal.Should().Be(RunRefusal.DelegationInvalid);
        }
    }

    [Fact]
    public async Task A_delegation_covers_only_its_kind_and_its_repositories_and_its_own_name()
    {
        var delegation = Delegate(TaskKind.MergePullRequest, "example/app");

        (await Run(await _world.Approved(Delegated(delegation, kinds: [TaskKind.ValidatePullRequest])), null)).Outcome.Refusal.Should().Be(RunRefusal.DelegationInvalid);
        (await Run(await _world.Approved(Delegated(delegation, "example/other")), null)).Outcome.Refusal.Should().Be(RunRefusal.DelegationInvalid);
        var wrongName = ExecutorFixture.Batch("example/app", "alice", delegation.Id, kinds: [TaskKind.MergePullRequest]);
        (await Run(await _world.Approved(wrongName), null)).Outcome.Refusal.Should().Be(RunRefusal.DelegationInvalid, "a delegated batch is approved by the delegation, not by a person's name");
    }

    [Fact]
    public async Task A_delegation_does_not_widen_the_rights_of_the_executor()
    {
        var delegation = Delegate(TaskKind.MergePullRequest, "*");
        var batch = await _world.Approved(Delegated(delegation, "other/app"));

        (await Run(batch, null)).Outcome.Refusal.Should().Be(RunRefusal.RepositoryNotAllowed);
    }

    [Fact]
    public async Task A_runner_that_fails_ends_its_task_as_refused_skips_the_merge_after_it_and_the_batch_still_ends()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch());
        var pipeline = _world.Pipeline([new FailingRunner(TaskKind.ValidatePullRequest), new DryRunTaskRunner(TaskKind.MergePullRequest)]);

        var result = await pipeline.RunAsync(new BatchDelivery(batch, _world.CodeAt()), () => false, TestContext.Current.CancellationToken);

        result.Outcome.Final.Should().Be(BatchState.Done);
        result.Outcome.Tasks.Select(task => task.Result).Should().Equal(TaskResult.Refused, TaskResult.Skipped);
        result.Events.Should().Contain(line => line.Type == RunEvents.TaskSkipped);
        result.Events.Should().Contain(line => line.Type == RunEvents.TaskRefused && line.Detail == "the runner failed (InvalidOperationException)");
    }

    [Fact]
    public async Task A_stop_engaged_during_the_run_ends_it_before_the_next_task()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch());
        var stop = false;
        var pipeline = _world.Pipeline([new StoppingRunner(TaskKind.ValidatePullRequest, () => stop = true), new DryRunTaskRunner(TaskKind.MergePullRequest)]);

        var result = await pipeline.RunAsync(new BatchDelivery(batch, _world.CodeAt()), () => stop, Ct);

        result.Outcome.Tasks.Select(task => task.Result).Should().Equal(TaskResult.DryRun, TaskResult.Skipped);
        result.Events.Should().Contain(line => line.Type == RunEvents.Stopped).And.Contain(line => line.Type == RunEvents.TaskSkipped);
        result.Events.Should().NotContain(line => line.Type == RunEvents.TaskDryRun && line.TaskId == batch.Tasks[1].Id, "the merge did not run after the stop");
    }

    private sealed class StoppingRunner(TaskKind kind, Action engage) : ITaskRunner
    {
        public TaskKind Kind => kind;

        public ValueTask<TaskOutcome> RunAsync(TaskSpec task, RunTrace trace, CancellationToken cancellationToken)
        {
            engage();
            return ValueTask.FromResult(new TaskOutcome(task.Id, TaskResult.DryRun, "ran, then the stop was engaged"));
        }
    }

    private sealed class FailingRunner(TaskKind kind) : ITaskRunner
    {
        public TaskKind Kind => kind;

        public ValueTask<TaskOutcome> RunAsync(TaskSpec task, RunTrace trace, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the runner broke");
    }
}
