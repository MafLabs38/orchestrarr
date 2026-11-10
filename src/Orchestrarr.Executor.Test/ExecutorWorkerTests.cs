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

    private ExecutorWorker Worker(string localStop = "") =>
        new(_controlPlane, _world.Pipeline(), _world.Store, new ExecutorOptions { Name = "executor-1", LocalStopFile = localStop, Rights = _world.Rights }, _world.Time, NullLogger<ExecutorWorker>.Instance);

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
    public async Task A_refused_batch_is_reported_like_any_other_end()
    {
        var rogue = ExecutorFixture.Batch();
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(new ClaimResult(ClaimStatus.Claimed, new BatchDelivery(rogue, _world.CodeAt())));

        (await Worker().StepAsync(Ct)).Should().BeTrue();

        await _controlPlane.Received(1).CompleteAsync(rogue.Id, Arg.Is<BatchOutcome>(outcome => outcome.Final == BatchState.Refused && outcome.Refusal == RunRefusal.JournalMismatch), Ct);
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

    [Fact]
    public async Task An_answer_that_cannot_be_read_does_not_stop_the_executor()
    {
        var asked = new TaskCompletionSource();
        var calls = 0;
        _controlPlane.ClaimAsync(Arg.Any<ExecutorHello>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (++calls == 1)
            {
                throw new JsonException("not a claim");
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

        asked.Task.IsCompleted.Should().BeTrue("the executor asked again after the unreadable answer");
        worker.ExecuteTask!.IsFaulted.Should().BeFalse("a control plane must not be able to stop the executor, and so restart it with empty counters");
        await worker.StopAsync(Ct);
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
