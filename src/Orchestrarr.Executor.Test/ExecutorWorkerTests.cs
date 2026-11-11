using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor.Test;

public sealed class ExecutorWorkerTests : IDisposable
{
    private readonly ExecutorFixture _world = new();
    private readonly IControlPlane _controlPlane = Substitute.For<IControlPlane>();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ExecutorWorkerTests()
    {
        _controlPlane.AppendEventsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<RunEvent>>(), Arg.Any<CancellationToken>()).Returns(QueueResult.Ok);
        _controlPlane.CompleteAsync(Arg.Any<Guid>(), Arg.Any<BatchOutcome>(), Arg.Any<CancellationToken>()).Returns(QueueResult.Ok);
    }

    public void Dispose() => _world.Dispose();

    // The live sending is slow by default: a test of something else must not see a partial send. The tests of the live sending ask for a fast one.
    private ExecutorWorker Worker(string localStop = "", ExecutorPipeline? pipeline = null, int liveMilliseconds = 60_000, int stopMilliseconds = 60_000) =>
        new(_controlPlane, pipeline ?? _world.Pipeline(), _world.Store, new ExecutorOptions { Name = "executor-1", LocalStopFile = localStop, Rights = _world.Rights, LiveSendMilliseconds = liveMilliseconds, StopPollMilliseconds = stopMilliseconds }, _world.Time, NullLogger<ExecutorWorker>.Instance);

    [Fact]
    public async Task A_batch_that_is_handed_over_is_run_then_its_trace_and_its_end_are_reported()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch());
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(new ClaimResult(ClaimStatus.Claimed, new BatchDelivery(batch, _world.CodeAt())));

        (await Worker().StepAsync(Ct)).Should().BeTrue("the next batch is asked for at once");

        await _controlPlane.Received(1).AppendEventsAsync(batch.Id, Arg.Is<IReadOnlyList<RunEvent>>(events => events.Count == 8 && RunEvent.Continues(batch.Digest(), 0, events)), Ct);
        await _controlPlane.Received(1).CompleteAsync(batch.Id, Arg.Is<BatchOutcome>(outcome => outcome.Final == BatchState.Done && outcome.Tasks.Count == 2), Ct);
    }

    [Fact]
    public async Task The_lines_of_a_run_that_goes_on_are_sent_while_it_goes_on_once_each_and_in_order()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch(kinds: [TaskKind.ValidatePullRequest]));
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(new ClaimResult(ClaimStatus.Claimed, new BatchDelivery(batch, _world.CodeAt())));
        var received = new List<RunEvent>();
        var requests = 0;
        var firstSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _controlPlane.AppendEventsAsync(batch.Id, Arg.Any<IReadOnlyList<RunEvent>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            lock (received)
            {
                received.AddRange(call.Arg<IReadOnlyList<RunEvent>>());
                requests++;
            }
            firstSent.TrySetResult();
            return QueueResult.Ok;
        });
        // The task waits for the first live send before it goes on: nothing here depends on how fast the machine is
        var pipeline = _world.Pipeline([new SlowRunner(firstSent.Task)]);

        await Worker(pipeline: pipeline, liveMilliseconds: 20).StepAsync(Ct);

        lock (received)
        {
            requests.Should().BeGreaterThan(1, "the lines were sent while the task was still running, and the rest at its end");
            RunEvent.Continues(batch.Digest(), 0, received).Should().BeTrue("each line once, in order, chained from the digest of the batch");
            received.Select(line => line.Type).Should().Contain(RunEvents.TaskPassed).And.EndWith(RunEvents.RunCompleted);
        }
    }

    [Fact]
    public async Task A_line_that_could_not_be_sent_while_the_run_went_on_is_sent_when_it_ends()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch(kinds: [TaskKind.ValidatePullRequest]));
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(new ClaimResult(ClaimStatus.Claimed, new BatchDelivery(batch, _world.CodeAt())));
        var received = new List<RunEvent>();
        var calls = 0;
        var failedOnce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _controlPlane.AppendEventsAsync(batch.Id, Arg.Any<IReadOnlyList<RunEvent>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            lock (received)
            {
                if (++calls == 1)
                {
                    failedOnce.TrySetResult();
                    throw new HttpRequestException("down");
                }
                received.AddRange(call.Arg<IReadOnlyList<RunEvent>>());
            }
            return QueueResult.Ok;
        });

        await Worker(pipeline: _world.Pipeline([new SlowRunner(failedOnce.Task)]), liveMilliseconds: 20).StepAsync(Ct);

        lock (received)
        {
            calls.Should().BeGreaterThanOrEqualTo(2, "the live send failed once, and the rest was sent at the end");
            RunEvent.Continues(batch.Digest(), 0, received).Should().BeTrue("nothing lost, nothing twice");
            received.Select(line => line.Type).Should().EndWith(RunEvents.RunCompleted);
        }
    }

    // A task that waits until it is cancelled, as a container that hangs until it is killed
    private sealed class BlockedUntilStopped : ITaskRunner
    {
        public TaskKind Kind => TaskKind.ValidatePullRequest;

        public async ValueTask<TaskOutcome> RunAsync(TaskSpec task, RunTrace trace, CancellationToken cancellationToken)
        {
            trace.Add(RunEvents.TaskStarted, task.Id, "waiting");
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new TaskOutcome(task.Id, TaskResult.Passed, null);
        }
    }

    [Fact]
    public async Task The_global_stop_engaged_while_a_task_runs_ends_the_task_and_the_run_is_reported()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch(kinds: [TaskKind.ValidatePullRequest]));
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(new ClaimResult(ClaimStatus.Claimed, new BatchDelivery(batch, _world.CodeAt())));
        _controlPlane.GlobalStopEngagedAsync(Arg.Any<CancellationToken>()).Returns(true);

        await Worker(pipeline: _world.Pipeline([new BlockedUntilStopped()]), stopMilliseconds: 20).StepAsync(Ct);

        await _controlPlane.Received(1).CompleteAsync(batch.Id, Arg.Is<BatchOutcome>(outcome => outcome.Final == BatchState.Done && outcome.Tasks.Single().Result == TaskResult.Skipped), Ct);
    }

    [Fact]
    public async Task A_control_plane_that_cannot_be_asked_three_times_in_a_row_counts_as_a_stop()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch(kinds: [TaskKind.ValidatePullRequest]));
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(new ClaimResult(ClaimStatus.Claimed, new BatchDelivery(batch, _world.CodeAt())));
        _controlPlane.GlobalStopEngagedAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<bool>(new HttpRequestException("down")));

        await Worker(pipeline: _world.Pipeline([new BlockedUntilStopped()]), stopMilliseconds: 20).StepAsync(Ct);

        await _controlPlane.Received(1).CompleteAsync(batch.Id, Arg.Is<BatchOutcome>(outcome => outcome.Tasks.Single().Result == TaskResult.Skipped), Ct);
        await _controlPlane.Received(3).GlobalStopEngagedAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_local_stop_file_created_while_a_task_runs_ends_it_too()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch(kinds: [TaskKind.ValidatePullRequest]));
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(new ClaimResult(ClaimStatus.Claimed, new BatchDelivery(batch, _world.CodeAt())));
        var stopFile = Path.Combine(_world.Folder, "STOP-now");

        var step = Worker(localStop: stopFile, pipeline: _world.Pipeline([new BlockedUntilStopped()]), stopMilliseconds: 20).StepAsync(Ct);
        await File.WriteAllTextAsync(stopFile, string.Empty, Ct);
        await step;

        await _controlPlane.Received(1).CompleteAsync(batch.Id, Arg.Is<BatchOutcome>(outcome => outcome.Tasks.Single().Result == TaskResult.Skipped), Ct);
    }

    // A task that ends by itself after a short while: it must not be stopped by a watcher that has nothing to report
    private sealed class EndsByItself : ITaskRunner
    {
        public TaskKind Kind => TaskKind.ValidatePullRequest;

        public bool ChangesNothing => true;

        public async ValueTask<TaskOutcome> RunAsync(TaskSpec task, RunTrace trace, CancellationToken cancellationToken)
        {
            await Task.Delay(250, cancellationToken);
            return new TaskOutcome(task.Id, TaskResult.Passed, "done");
        }
    }

    [Fact]
    public async Task A_control_plane_that_says_there_is_no_stop_does_not_stop_the_run()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch(kinds: [TaskKind.ValidatePullRequest]));
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(new ClaimResult(ClaimStatus.Claimed, new BatchDelivery(batch, _world.CodeAt())));
        _controlPlane.GlobalStopEngagedAsync(Arg.Any<CancellationToken>()).Returns(false);

        await Worker(pipeline: _world.Pipeline([new EndsByItself()]), stopMilliseconds: 20).StepAsync(Ct);

        await _controlPlane.Received(1).CompleteAsync(batch.Id, Arg.Is<BatchOutcome>(outcome => outcome.Tasks.Single().Result == TaskResult.Passed), Ct);
        await _controlPlane.Received().GlobalStopEngagedAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failures_to_read_the_stop_that_are_not_three_in_a_row_do_not_stop_the_run()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch(kinds: [TaskKind.ValidatePullRequest]));
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(new ClaimResult(ClaimStatus.Claimed, new BatchDelivery(batch, _world.CodeAt())));
        // Two failures, an answer, two failures, then answers: the count starts again at each answer
        _controlPlane.GlobalStopEngagedAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromException<bool>(new HttpRequestException("down")), Task.FromException<bool>(new HttpRequestException("down")), Task.FromResult(false),
            Task.FromException<bool>(new HttpRequestException("down")), Task.FromException<bool>(new HttpRequestException("down")), Task.FromResult(false));

        await Worker(pipeline: _world.Pipeline([new EndsByItself()]), stopMilliseconds: 20).StepAsync(Ct);

        await _controlPlane.Received(1).CompleteAsync(batch.Id, Arg.Is<BatchOutcome>(outcome => outcome.Tasks.Single().Result == TaskResult.Passed), Ct);
    }

    [Fact]
    public async Task A_live_send_that_the_control_plane_refuses_is_not_counted_as_sent_and_nothing_is_lost_or_sent_twice()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch(kinds: [TaskKind.ValidatePullRequest]));
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(new ClaimResult(ClaimStatus.Claimed, new BatchDelivery(batch, _world.CodeAt())));
        var received = new List<RunEvent>();
        var calls = 0;
        var refusedOnce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _controlPlane.AppendEventsAsync(batch.Id, Arg.Any<IReadOnlyList<RunEvent>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            lock (received)
            {
                if (++calls == 1)
                {
                    refusedOnce.TrySetResult();
                    return QueueResult.Invalid;
                }
                received.AddRange(call.Arg<IReadOnlyList<RunEvent>>());
            }
            return QueueResult.Ok;
        });

        await Worker(pipeline: _world.Pipeline([new SlowRunner(refusedOnce.Task)]), liveMilliseconds: 20).StepAsync(Ct);

        lock (received)
        {
            calls.Should().BeGreaterThanOrEqualTo(2);
            RunEvent.Continues(batch.Digest(), 0, received).Should().BeTrue("the refused lines were sent again at the end, once, in order");
            received.Select(line => line.Type).Should().EndWith(RunEvents.RunCompleted);
        }
    }

    [Fact]
    public async Task The_hello_says_only_the_kinds_the_executor_has_a_runner_for_whatever_its_rights_list_names()
    {
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(new ClaimResult(ClaimStatus.Nothing));
        _world.Rights = new RightsOptions { Kinds = [TaskKind.ValidatePullRequest, TaskKind.MergePullRequest, TaskKind.RepairPullRequest], Repositories = ["example/*"] };

        await Worker().StepAsync(Ct);

        await _controlPlane.Received(1).ClaimAsync(Arg.Is<ExecutorHello>(hello => hello.Kinds.SequenceEqual(new[] { TaskKind.ValidatePullRequest, TaskKind.MergePullRequest })), Ct);
    }

    // A task that takes a while and writes lines as it goes
    private sealed class SlowRunner(Task gate) : ITaskRunner
    {
        public TaskKind Kind => TaskKind.ValidatePullRequest;

        public bool ChangesNothing => true;

        public async ValueTask<TaskOutcome> RunAsync(TaskSpec task, RunTrace trace, CancellationToken cancellationToken)
        {
            for (var step = 1; step <= 2; step++)
            {
                trace.Add(RunEvents.TaskStep, task.Id, $"step {step}");
            }
            // Until the control plane has been asked once (or a generous limit), then the last lines
            await Task.WhenAny(gate, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
            trace.Add(RunEvents.TaskStep, task.Id, "step 3");
            trace.Add(RunEvents.TaskPassed, task.Id, "done");
            return new TaskOutcome(task.Id, TaskResult.Passed, "done");
        }
    }

    [Fact]
    public async Task A_refused_batch_is_reported_like_any_other_end()
    {
        var rogue = ExecutorFixture.Batch();
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(new ClaimResult(ClaimStatus.Claimed, new BatchDelivery(rogue, _world.CodeAt())));

        (await Worker().StepAsync(Ct)).Should().BeTrue();

        await _controlPlane.Received(1).CompleteAsync(rogue.Id, Arg.Is<BatchOutcome>(outcome => outcome.Final == BatchState.Refused && outcome.Refusal == RunRefusal.JournalMismatch), Ct);
    }

    [Fact]
    public async Task A_result_that_could_not_be_reported_is_sent_again_and_the_batch_is_not_run_twice()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch());
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(new ClaimResult(ClaimStatus.Claimed, new BatchDelivery(batch, _world.CodeAt())), new ClaimResult(ClaimStatus.Nothing));
        _controlPlane.CompleteAsync(batch.Id, Arg.Any<BatchOutcome>(), Arg.Any<CancellationToken>()).Returns(_ => throw new HttpRequestException("down"), _ => QueueResult.Ok);
        var worker = Worker();

        var first = async () => await worker.StepAsync(Ct);
        await first.Should().ThrowAsync<HttpRequestException>();
        (await worker.StepAsync(Ct)).Should().BeFalse("the second turn only reports and finds nothing to claim");

        await _controlPlane.Received(2).CompleteAsync(batch.Id, Arg.Is<BatchOutcome>(outcome => outcome.Final == BatchState.Done), Ct);
        await _controlPlane.Received(2).ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_hello_says_whether_this_executor_checks_the_code(bool requires)
    {
        var worker = new ExecutorWorker(_controlPlane, _world.Pipeline(requireSecondFactor: requires), _world.Store, new ExecutorOptions { Name = "executor-1", Rights = _world.Rights }, _world.Time, NullLogger<ExecutorWorker>.Instance);
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(new ClaimResult(ClaimStatus.Nothing));

        await worker.StepAsync(Ct);

        await _controlPlane.Received(1).ClaimAsync(Arg.Is<ExecutorHello>(hello => hello.SecondFactorRequired == requires), Ct);
    }

    [Fact]
    public async Task With_no_stop_file_configured_a_STOP_file_next_to_the_executors_own_file_stops_it()
    {
        var options = new ExecutorOptions { Name = "executor-1", DataPath = Path.Combine(_world.Folder, "executor.db"), Rights = _world.Rights };
        var worker = new ExecutorWorker(_controlPlane, _world.Pipeline(), _world.Store, options, _world.Time, NullLogger<ExecutorWorker>.Instance);
        await File.WriteAllTextAsync(Path.Combine(_world.Folder, "STOP"), string.Empty, Ct);

        (await worker.StepAsync(Ct)).Should().BeFalse();

        await _controlPlane.DidNotReceive().ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stop_file_created_during_a_run_ends_it_before_the_next_task()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch());
        var stop = Path.Combine(_world.Folder, "STOP");
        var pipeline = _world.Pipeline([new CreatesTheStopFile(TaskKind.ValidatePullRequest, stop), new DryRunTaskRunner(TaskKind.MergePullRequest)]);
        var options = new ExecutorOptions { Name = "executor-1", LocalStopFile = stop, Rights = _world.Rights };
        var worker = new ExecutorWorker(_controlPlane, pipeline, _world.Store, options, _world.Time, NullLogger<ExecutorWorker>.Instance);
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(new ClaimResult(ClaimStatus.Claimed, new BatchDelivery(batch, _world.CodeAt())));

        await worker.StepAsync(Ct);

        await _controlPlane.Received(1).CompleteAsync(batch.Id, Arg.Is<BatchOutcome>(outcome => outcome.Tasks.Count == 2 && outcome.Tasks[1].Result == TaskResult.Skipped), Ct);
    }

    private sealed class CreatesTheStopFile(TaskKind kind, string stopFile) : ITaskRunner
    {
        public TaskKind Kind => kind;

        public ValueTask<TaskOutcome> RunAsync(TaskSpec task, RunTrace trace, CancellationToken cancellationToken)
        {
            File.WriteAllText(stopFile, string.Empty);
            return ValueTask.FromResult(new TaskOutcome(task.Id, TaskResult.DryRun, "ran, then the stop file appeared"));
        }
    }

    [Theory]
    [InlineData(ClaimStatus.Nothing)]
    [InlineData(ClaimStatus.Stopped)]
    [InlineData(ClaimStatus.Unavailable)]
    public async Task When_nothing_is_handed_over_nothing_is_reported(ClaimStatus status)
    {
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(new ClaimResult(status));

        (await Worker().StepAsync(Ct)).Should().BeFalse();

        await _controlPlane.DidNotReceive().CompleteAsync(Arg.Any<Guid>(), Arg.Any<BatchOutcome>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(typeof(JsonException))]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(NullReferenceException))]
    public async Task An_answer_that_cannot_be_read_or_a_control_plane_that_fails_does_not_stop_the_executor(Type failure)
    {
        var asked = new TaskCompletionSource();
        var calls = 0;
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (++calls == 1)
            {
                throw (Exception)Activator.CreateInstance(failure)!;
            }
            asked.TrySetResult();
            return Task.FromResult(new ClaimResult(ClaimStatus.Nothing));
        });
        using var worker = Worker();

        await worker.StartAsync(Ct);
        for (var turn = 0; turn < 200 && !asked.Task.IsCompleted; turn++)
        {
            _world.Time.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(10, Ct);
        }
        await worker.StopAsync(Ct);

        asked.Task.IsCompleted.Should().BeTrue("the executor asked again after the failure");
        worker.ExecuteTask!.IsFaulted.Should().BeFalse("a control plane must not be able to stop the executor, and so restart it with empty counters");
    }

    [Fact]
    public async Task The_local_stop_file_stops_the_executor_before_it_asks_for_anything()
    {
        var stop = Path.Combine(_world.Folder, "STOP");
        await File.WriteAllTextAsync(stop, string.Empty, Ct);

        (await Worker(stop).StepAsync(Ct)).Should().BeFalse();

        await _controlPlane.DidNotReceive().ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task What_the_executor_says_of_itself_is_its_name_its_rights_and_the_delegations_in_force()
    {
        _world.Store.AddDelegation(new Delegation(Guid.NewGuid(), "alice", TaskKind.MergePullRequest, "example/*", ExecutorFixture.Start.AddDays(3), 5, ExecutorFixture.Start, null));
        ExecutorHello? hello = null;
        _controlPlane.ClaimAsync(Arg.Do<ExecutorHello>(sent => hello = sent), Arg.Any<CancellationToken>()).Returns(new ClaimResult(ClaimStatus.Nothing));

        await Worker().StepAsync(Ct);

        hello.Should().NotBeNull();
        hello.Name.Should().Be("executor-1");
        hello.Kinds.Should().BeEquivalentTo([TaskKind.ValidatePullRequest, TaskKind.MergePullRequest]);
        hello.Repositories.Should().Equal("example/*");
        hello.Delegations.Should().ContainSingle().Which.Kind.Should().Be(TaskKind.MergePullRequest);
    }

    [Fact]
    public async Task A_trace_that_the_control_plane_refuses_does_not_stop_the_end_from_being_reported()
    {
        var batch = await _world.Approved(ExecutorFixture.Batch());
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(new ClaimResult(ClaimStatus.Claimed, new BatchDelivery(batch, _world.CodeAt())));
        _controlPlane.AppendEventsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<RunEvent>>(), Arg.Any<CancellationToken>()).Returns(QueueResult.Invalid);

        await Worker().StepAsync(Ct);

        await _controlPlane.Received(1).CompleteAsync(batch.Id, Arg.Any<BatchOutcome>(), Ct);
        _world.Store.Trace(batch.Id).Should().HaveCount(8, "the executor keeps its own trace whatever the control plane says");
    }
}

public sealed class ControlPlaneClientTests
{
    [Theory]
    [InlineData("https://orchestrarr.homelab.example")]
    [InlineData("http://localhost:5080")]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("http://10.0.0.50:5080")]
    [InlineData("http://10.0.0.5")]
    [InlineData("http://172.16.0.9")]
    public void The_token_goes_only_over_https_or_inside_the_local_network(string url)
    {
        var create = () => new ControlPlaneClient(new HttpClient(), new ControlPlaneOptions { Url = url, Token = "secret-token" });

        create.Should().NotThrow();
    }

    [Theory]
    [InlineData("http://orchestrarr.homelab.example")]
    [InlineData("http://8.8.8.8")]
    [InlineData("http://172.32.0.1")]
    [InlineData("not an address")]
    [InlineData("")]
    public void Plain_http_to_the_outside_and_a_bad_address_are_refused(string url)
    {
        var create = () => new ControlPlaneClient(new HttpClient(), new ControlPlaneOptions { Url = url, Token = "secret-token" });

        create.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Without_a_token_the_client_does_not_start()
    {
        var create = () => new ControlPlaneClient(new HttpClient(), new ControlPlaneOptions { Url = "https://orchestrarr.homelab.example" });

        create.Should().Throw<InvalidOperationException>().WithMessage("*Token*");
    }

    [Fact]
    public void The_token_may_come_from_a_file()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "token-from-a-file\n");
            var http = new HttpClient();

            _ = new ControlPlaneClient(http, new ControlPlaneOptions { Url = "https://orchestrarr.homelab.example", TokenFile = file });

            http.DefaultRequestHeaders.Authorization!.Parameter.Should().Be("token-from-a-file");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
