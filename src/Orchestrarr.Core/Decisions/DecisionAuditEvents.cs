namespace Orchestrarr.Core.Decisions;

/// <summary>The names, in the audit trail, of what is decided about the rows of the table (ADR 0027).</summary>
public static class DecisionAuditEvents
{
    public const string ProblemSkipped = "problem.skipped";
    public const string ProblemUnskipped = "problem.unskipped";
}
