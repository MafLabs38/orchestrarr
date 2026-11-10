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
    private (Guid BatchId, PipelineResult Result)? _unreported;

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
            await ReportAsync(pending.BatchId, pending.Result, cancellationToken);
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
        var result = await pipeline.RunAsync(delivery, options.IsLocallyStopped, cancellationToken);
        logger.LogInformation("Batch {Batch} ended {State}", delivery.Batch.Id, result.Outcome.Final);
        // The batch is registered as seen: it cannot be run again, so what became of it is kept until the control plane has been told
        _unreported = (delivery.Batch.Id, result);
        await ReportAsync(delivery.Batch.Id, result, cancellationToken);
        _unreported = null;
        return true;
    }

    private async Task ReportAsync(Guid batchId, PipelineResult result, CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < result.Events.Count; offset += EventsPerRequest)
        {
            var sent = await controlPlane.AppendEventsAsync(batchId, [.. result.Events.Skip(offset).Take(EventsPerRequest)], cancellationToken);
            if (sent != QueueResult.Ok)
            {
                logger.LogError("The control plane refused the trace of {Batch}: {Result}", batchId, sent);
                break;
            }
        }
        var completed = await controlPlane.CompleteAsync(batchId, result.Outcome, cancellationToken);
        if (completed != QueueResult.Ok)
        {
            logger.LogError("The control plane did not accept the end of {Batch}: {Result}", batchId, completed);
        }
    }

    private ExecutorHello Hello() => new(
        options.Name,
        typeof(ExecutorWorker).Assembly.GetName().Version?.ToString() ?? "0",
        options.Rights.Kinds,
        options.Rights.Repositories,
        [.. store.Delegations(includeEnded: false, time.GetUtcNow()).Select(delegation => delegation.ToInfo())]);
}
