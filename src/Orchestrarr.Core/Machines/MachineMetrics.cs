namespace Orchestrarr.Core.Machines;

/// <summary>
/// What a machine did over a window, as for equipment of a fab: the time in each state, how much of the time it could produce (availability), how much it produced (utilisation), the number of lots, and the repairs.
/// <c>Availability</c> is the share of the window that the machine was not Down; <c>Utilisation</c> the share of it that a mission held the machine.
/// </summary>
public sealed record MachineStats(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyDictionary<MachineState, TimeSpan> InState,
    double Availability,
    double Utilisation,
    int Lots,
    TimeSpan MeanLot,
    int DownEvents,
    TimeSpan MeanTimeToRepair);

public static class MachineMetrics
{
    /// <summary>
    /// The statistics of a machine from its transitions (oldest first). The window starts at <paramref name="from"/> or at the first transition if that is later: nothing is said of the time before the machine was known.
    /// </summary>
    public static MachineStats Compute(IReadOnlyList<MachineTransition> transitions, DateTimeOffset from, DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        var inState = Enum.GetValues<MachineState>().ToDictionary(state => state, _ => TimeSpan.Zero);
        var lots = 0;
        var lotTime = TimeSpan.Zero;
        var downEvents = 0;
        var repairTime = TimeSpan.Zero;
        var repairs = 0;
        if (transitions.Count == 0 || to <= from)
        {
            return new MachineStats(from, to, inState, 0, 0, 0, TimeSpan.Zero, 0, TimeSpan.Zero);
        }
        var start = from > transitions[0].At ? from : transitions[0].At;
        for (var index = 0; index < transitions.Count; index++)
        {
            var current = transitions[index];
            var end = index + 1 < transitions.Count ? transitions[index + 1].At : to;
            var segmentStart = current.At < start ? start : current.At;
            var segmentEnd = end > to ? to : end;
            if (segmentEnd > segmentStart)
            {
                var length = segmentEnd - segmentStart;
                inState[current.To] += length;
                if (current.To == MachineState.InProcess)
                {
                    // A lot is a spell in process that has some time inside the window, whenever it began: the mean is the time in process over those lots
                    lotTime += length;
                    lots++;
                }
            }
            // The events are counted when they begin inside the window; a repair is the time of a Down spell that ended inside it
            if (current.At >= start && current.At <= to && current.To == MachineState.Down)
            {
                downEvents++;
                if (index + 1 < transitions.Count && transitions[index + 1].At <= to)
                {
                    repairTime += transitions[index + 1].At - current.At;
                    repairs++;
                }
            }
        }
        var total = to - start;
        var down = inState[MachineState.Down];
        var availability = total > TimeSpan.Zero ? 1 - (down / total) : 0;
        var utilisation = total > TimeSpan.Zero ? inState[MachineState.InProcess] / total : 0;
        return new MachineStats(start, to, inState, availability, utilisation, lots, lots > 0 ? lotTime / lots : TimeSpan.Zero, downEvents, repairs > 0 ? repairTime / repairs : TimeSpan.Zero);
    }
}
