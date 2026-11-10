using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Host.Tasks;

/// <summary>
/// Ends the batches that are past their time, on a timer (ADR 0017). It does not wait for an executor to ask for work: with no executor
/// connected, or while the global stop is engaged, an approval would stay waiting for ever, fill the queue, block the same decision from being
/// given again and keep its code. Expiring starts nothing, so the stop does not hold it back.
/// </summary>
public sealed class BatchExpirySweeper(IBatchQueue queue, TaskOptions options, TimeProvider time, ILogger<BatchExpirySweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.ExpirySweepInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!await queue.ExpireDueAsync(stoppingToken))
                {
                    logger.LogWarning("The batches past their time could not be ended; the next pass will try again");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping
        }
    }
}
