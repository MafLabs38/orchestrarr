namespace Orchestrarr.Core.Missions;

/// <summary>
/// The priority of a problem in the flow, a number from 0 to 100 (the maintainer, 2026-11-24, ADR 0050): **50 is normal**, an urgent problem starts at **70** and goes up by one each time it is raised again, up to 100. A number and not
/// a few names, so that the flow can later be told apart more finely without changing what it is. The priority orders the work (the line before a start, the next mission a runner is given); it never grants a power, and a running
/// mission is never interrupted for it (a lot on a machine ends its step).
/// </summary>
public static class FlowPriority
{
    public const int Lowest = 0;
    public const int Normal = 50;
    public const int FirstUrgent = 70;
    public const int Highest = 100;

    /// <summary>The priority after one more urgent event: the first one sets 70, the next ones add one, never past 100.</summary>
    public static int Raised(int current) => Math.Min(Highest, Math.Max(FirstUrgent, current + 1));
}
