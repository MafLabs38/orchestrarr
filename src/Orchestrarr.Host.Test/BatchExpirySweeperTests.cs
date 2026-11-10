using Microsoft.Extensions.Logging.Abstractions;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Host.Tasks;

namespace Orchestrarr.Host.Test;

/// <summary>Batches past their time end on a timer, whether or not an executor asks for work (ADR 0017).</summary>
public sealed class BatchExpirySweeperTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task The_sweeper_asks_the_queue_to_end_the_batches_past_their_time_again_and_again_until_it_stops()
    {
        var queue = new CountingQueue(passes: 2);
        var sweeper = new BatchExpirySweeper(queue, new TaskOptions { ExpirySweepInterval = TimeSpan.FromMilliseconds(20) }, TimeProvider.System, NullLogger<BatchExpirySweeper>.Instance);

        await sweeper.StartAsync(TestContext.Current.CancellationToken);
        await queue.Reached.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await sweeper.StopAsync(TestContext.Current.CancellationToken);

        queue.Passes.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task A_pass_that_could_not_run_does_not_stop_the_sweeper()
    {
        var queue = new CountingQueue(passes: 2) { Succeeds = false };
        var sweeper = new BatchExpirySweeper(queue, new TaskOptions { ExpirySweepInterval = TimeSpan.FromMilliseconds(20) }, TimeProvider.System, NullLogger<BatchExpirySweeper>.Instance);

        await sweeper.StartAsync(TestContext.Current.CancellationToken);
        await queue.Reached.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await sweeper.StopAsync(TestContext.Current.CancellationToken);

        queue.Passes.Should().BeGreaterThanOrEqualTo(2);
    }

    private sealed class CountingQueue(int passes) : IBatchQueue
    {
        private int _passes;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Succeeds { get; init; } = true;

        public int Passes => Volatile.Read(ref _passes);

        public ValueTask<bool> ExpireDueAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _passes) >= passes)
            {
                Reached.TrySetResult();
            }
            return ValueTask.FromResult(Succeeds);
        }

        public ValueTask<ClaimResult> ClaimAsync(string executor, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<QueueResult> AppendEventsAsync(Guid batchId, string executor, IReadOnlyList<RunEvent> events, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<QueueResult> CompleteAsync(Guid batchId, string executor, BatchOutcome outcome, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IReadOnlyList<RunSummary> Runs(int limit) => throw new NotSupportedException();

        public RunDetail? Find(Guid batchId) => throw new NotSupportedException();
    }
}
