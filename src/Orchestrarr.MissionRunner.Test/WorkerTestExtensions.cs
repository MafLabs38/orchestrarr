namespace Orchestrarr.MissionRunner.Test;

/// <summary>What the tests of the worker say in one line: the lanes taken one after the other, as one turn of a runner that has no concurrency.</summary>
internal static class WorkerTestExtensions
{
    public static async Task<bool> TakeOneAsync(this MissionRunnerWorker worker, CancellationToken cancellationToken)
    {
        foreach (var lane in worker.LanesOf())
        {
            if (await worker.TakeOneAsync(lane, cancellationToken))
            {
                return true;
            }
        }
        return false;
    }

    public static async Task MaintainMachinesAsync(this MissionRunnerWorker worker, CancellationToken cancellationToken)
    {
        foreach (var machine in worker.LanesOf().Select(lane => lane.Machine).OfType<string>())
        {
            await worker.MaintainSafelyAsync(machine, cancellationToken);
        }
    }
}
