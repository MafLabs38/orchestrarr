namespace Orchestrarr.Core.Missions;

/// <summary>
/// The bounds of the loop (ADR 0036): a defect found after a world opens a new mandate to repair it, but not forever. A mission is sent back a limited number of times,
/// a problem gets a limited number of mandates and a limited time, and a merge of it that turns the integration branch red is fixed forward a limited number of times (ADR 0055: whether each one is worth it is the controller's
/// judgement, and this is the block above it); on reaching a limit the work stops and the maintainer is told.
/// </summary>
public sealed record LoopPolicy(int MaxSendBacksPerMission, int MaxMandatesPerProblem, TimeSpan MaxTimePerProblem, int MaxFixesForwardPerProblem = 3)
{
    public static LoopPolicy Default { get; } = new(3, 8, TimeSpan.FromHours(6));

    /// <summary>Whether the problem may be given one more mandate.</summary>
    public LoopVerdict Check(int mandatesIssued, TimeSpan elapsed)
    {
        if (mandatesIssued >= MaxMandatesPerProblem)
        {
            return LoopVerdict.TooManyMandates;
        }
        return elapsed >= MaxTimePerProblem ? LoopVerdict.TooLong : LoopVerdict.Allowed;
    }
}

public enum LoopVerdict
{
    Allowed,
    TooManyMandates,
    TooLong,
}
