namespace Orchestrarr.Core.Missions;

/// <summary>
/// Whether the maintainer's approval of a plan entry is proved (ADR 0031 point 7): the first mandate of a problem is never issued on a validation that is only a name. The proof is the
/// second factor, recorded through the queue of approvals; until the plan service links the two, the host answers no.
/// </summary>
public interface IApprovalCheck
{
    bool IsProved(PlanEntry entry);
}

/// <summary>The global stop, as the conductor reads it: while it is engaged no mandate is issued, and the sweep stops what is running.</summary>
public interface IStopSignal
{
    bool Engaged { get; }
}

/// <summary>The audit trail of the missions: every mandate issued or revoked, every decision of the controller with its reasons, every send-back and end (FR-SAF, rule 5 of CLAUDE.md).</summary>
public interface IMissionAudit
{
    /// <summary>False when the entry could not be written. A power is never granted on an entry that cannot be written; a power is always withdrawn, whatever the trail says.</summary>
    bool Record(string eventName, string subject, string? detail);
}

public static class MissionAuditEvents
{
    public const string ProblemStarted = "problem.started";
    public const string ProblemClosed = "problem.closed";
    public const string ProblemFailed = "problem.failed";
    public const string ProblemEscalated = "problem.escalated";
    public const string MandateIssued = "mandate.issued";
    public const string MandateRevoked = "mandate.revoked";
    public const string MissionHandedOver = "mission.handed-over";
    public const string MissionAccepted = "mission.accepted";
    public const string MissionSentBack = "mission.sent-back";
    public const string MissionEnded = "mission.ended";
    public const string ControllerDecided = "controller.decided";
}
