using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor;

/// <summary>
/// The loop of the executor: ask for a batch, check and run it, report the trace and how it ended, ask again. It stops asking while the local
/// stop file exists, and backs off when the control plane cannot be reached. A refusal is an outcome like another: it is reported, not thrown.
/// </summary>
public sealed class ExecutorWorker(IControlPlane controlPlane, ExecutorPipeline pipeline, ExecutorStore store, ExecutorOptions options, TimeProvider time, ILogger<ExecutorWorker> logger) : BackgroundService
{
    private Unreported? _unreported;

    private const int EventsPerRequest = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                worked = await StepAsync(stoppingToken);
                failures = 0;
            }
            catch (Exception failure) when (!stoppingToken.IsCancellationRequested)
            {
                // Whatever went wrong (no answer, or an answer that cannot be read), the executor stays up and tries again: a control plane
                // that could stop it with a malformed answer could also restart it, and a restart forgets the counters of the second factor.
                failures++;
                logger.LogWarning(failure, "The control plane cannot be reached or answered something unreadable, trying again");
            }
            var wait = worked ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(30, options.PollSeconds * (1 + failures)));
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, time, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One turn: true when a batch was handled, so that the next one is asked for at once.</summary>
    internal async Task<bool> StepAsync(CancellationToken cancellationToken)
    {
        if (_unreported is { } pending)
        {
            // A report that could not be sent comes before anything else; if this fails again it is kept for the next turn
            await ReportAsync(pending, cancellationToken);
            _unreported = null;
        }

        if (options.IsLocallyStopped())
        {
            logger.LogWarning("The local stop file {File} exists: nothing is asked for", options.StopFile);
            return false;
        }

        var claim = await controlPlane.ClaimAsync(Hello(), cancellationToken);
        if (claim.Status != ClaimStatus.Claimed || claim.Delivery is null)
        {
            if (claim.Status == ClaimStatus.Stopped)
            {
                logger.LogWarning("The global stop is engaged");
            }
            return false;
        }

        var delivery = claim.Delivery;
        // The lines go to the control plane while the run goes on, so that a person sees what happens as it happens
        var live = new LiveSender(controlPlane, delivery.Batch.Id, logger);
        using var running = new CancellationTokenSource();
        // The stop is read while the run goes on, not only between tasks: it cancels the task that is running (its runner kills the container)
        using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watcher = new StopWatcher(controlPlane, options, logger, stopSource, delivery.Batch.Id);
        var flushing = live.RunAsync(TimeSpan.FromMilliseconds(Math.Max(10, options.LiveSendMilliseconds)), running.Token);
        var watching = watcher.RunAsync(TimeSpan.FromMilliseconds(Math.Max(10, options.StopPollMilliseconds)), running.Token);
        PipelineResult result;
        try
        {
            result = await pipeline.RunAsync(delivery, () => options.IsLocallyStopped() || watcher.Engaged, stopSource.Token, live.Add);
        }
        finally
        {
            await running.CancelAsync();
            await Task.WhenAll(flushing, watching);
        }
        logger.LogInformation("Batch {Batch} ended {State}", delivery.Batch.Id, result.Outcome.Final);
        // The batch is registered as seen: it cannot be run again, so what became of it is kept until the control plane has been told
        _unreported = new Unreported(delivery.Batch.Id, result) { Sent = live.Sent };
        await ReportAsync(_unreported, cancellationToken);
        _unreported = null;
        return true;
    }

    // An answer that is not Ok is final (the control plane does not know the batch, or it is not this executor's, or the lines do not follow):
    // sending again would change nothing. Only a call that fails is kept for the next turn, and the lines already accepted are not sent twice.
    private async Task ReportAsync(Unreported report, CancellationToken cancellationToken)
    {
        var events = report.Result.Events;
        while (report.Sent < events.Count)
        {
            var chunk = events.Skip(report.Sent).Take(EventsPerRequest).ToList();
            var sent = await controlPlane.AppendEventsAsync(report.BatchId, chunk, cancellationToken);
            if (sent != QueueResult.Ok)
            {
                logger.LogError("The control plane refused the trace of {Batch}: {Result}", report.BatchId, sent);
                break;
            }
            report.Sent += chunk.Count;
        }
        var completed = await controlPlane.CompleteAsync(report.BatchId, report.Result.Outcome, cancellationToken);
        if (completed != QueueResult.Ok)
        {
            logger.LogError("The control plane did not accept the end of {Batch}: {Result}", report.BatchId, completed);
        }
    }

    /// <summary>
    /// Reads the stops while a run goes on: the local stop file and the global stop of the control plane. When one is engaged it cancels the run's token, so
    /// that the task that is running ends (its runner kills its container) and nothing after it starts. A control plane that cannot be asked three times
    /// in a row counts as a stop engaged: unknown means stopped, as everywhere else in the product.
    /// </summary>
    private sealed class StopWatcher(IControlPlane controlPlane, ExecutorOptions options, ILogger logger, CancellationTokenSource stopSource, Guid batchId)
    {
        private const int FailuresBeforeStop = 3;

        private volatile bool _engaged;

        public bool Engaged => _engaged;

        public async Task RunAsync(TimeSpan interval, CancellationToken finished)
        {
            var failures = 0;
            try
            {
                while (!_engaged)
                {
                    await Task.Delay(interval, finished);
                    if (options.IsLocallyStopped())
                    {
                        Engage("the local stop file exists");
                        return;
                    }
                    try
                    {
                        // A request that hangs is a failure after a few seconds, not after the 30 of the client: three of them make a stop
                        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        if (await controlPlane.GlobalStopEngagedAsync(limit.Token))
                        {
                            Engage("the global stop is engaged");
                            return;
                        }
                        // The same poll tells the control plane that this executor is alive (an empty send renews the delivery, whatever the run prints), and learns
                        // that it no longer holds the batch (it expired, or the stop ended it): a run the host has given up on is not carried on
                        if (await controlPlane.AppendEventsAsync(batchId, [], limit.Token) is QueueResult.NotYours or QueueResult.NotFound)
                        {
                            Engage("the control plane no longer holds this batch for this executor");
                            return;
                        }
                        failures = 0;
                    }
                    catch (Exception failure)
                    {
                        failures++;
                        logger.LogWarning(failure, "The global stop could not be read ({Failures} in a row)", failures);
                    }
                    if (failures >= FailuresBeforeStop)
                    {
                        Engage("the global stop cannot be read");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The run ended: nothing left to watch
            }
        }

        private void Engage(string reason)
        {
            _engaged = true;
            logger.LogWarning("Stopping the run: {Reason}", reason);
            stopSource.Cancel();
        }
    }

    /// <summary>
    /// Sends the lines of a run as they are written: every interval, what is waiting goes in one request, in order. A request that does not
    /// succeed ends the live sending for this run (the rest is sent when the run ends, as before), so that a line is not sent out of order; a line whose answer was lost may be sent again at the end, and the control plane skips what it already holds.
    /// </summary>
    private sealed class LiveSender(IControlPlane controlPlane, Guid batchId, ILogger logger)
    {
        private readonly Lock _gate = new();
        private readonly List<RunEvent> _waiting = [];
        private volatile bool _failed;

        public int Sent { get; private set; }

        public void Add(RunEvent line)
        {
            lock (_gate)
            {
                _waiting.Add(line);
            }
        }

        public async Task RunAsync(TimeSpan interval, CancellationToken stop)
        {
            try
            {
                while (!_failed)
                {
                    await Task.Delay(interval, stop);
                    await FlushAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // The run ended: what is still waiting is sent with the end of the run
            }
        }

        private async Task FlushAsync()
        {
            List<RunEvent> chunk;
            lock (_gate)
            {
                chunk = [.. _waiting.Take(EventsPerRequest)];
            }
            if (chunk.Count == 0)
            {
                return;
            }
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                if (await controlPlane.AppendEventsAsync(batchId, chunk, limit.Token) != QueueResult.Ok)
                {
                    _failed = true;
                    return;
                }
            }
            catch (Exception failure)
            {
                // Whatever went wrong (no answer, a timeout, an answer that cannot be read), the live feed is best effort: it must never hide the outcome of a run that
                // has happened. It stops, and the rest is sent with the end of the run.
                logger.LogWarning(failure, "A line of the trace could not be sent while the run goes on; the rest is sent at its end");
                _failed = true;
                return;
            }
            lock (_gate)
            {
                _waiting.RemoveRange(0, chunk.Count);
            }
            Sent += chunk.Count;
        }
    }

    private sealed class Unreported(Guid batchId, PipelineResult result)
    {
        public Guid BatchId { get; } = batchId;

        public PipelineResult Result { get; } = result;

        public int Sent { get; set; }
    }

    private ExecutorHello Hello() => new(
        options.Name,
        typeof(ExecutorWorker).Assembly.GetName().Version?.ToString() ?? "0",
        // What it accepts is what it has a runner for, whatever its rights list names: the table never promises more than would run
        [.. options.Rights.Kinds.Where(pipeline.RunnerKinds.Contains)],
        options.Rights.Repositories,
        [.. store.Delegations(includeEnded: false, time.GetUtcNow()).Select(delegation => delegation.ToInfo())],
        pipeline.SecondFactorRequired);
}
