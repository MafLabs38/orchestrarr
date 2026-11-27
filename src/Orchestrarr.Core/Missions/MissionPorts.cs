namespace Orchestrarr.Core.Missions;

/// <summary>
/// The certificate of what the maintainer approved for a plan entry, issued by the approval authority (ADR 0031 point 7), or null when none was issued: the first mandate of a problem is never issued
/// on a validation that is only a name. Until the plan service links a plan entry to a certificate, the host has none to give.
/// </summary>
public interface IApprovalSource
{
    SignedApproval? For(PlanEntry entry);
}

/// <summary>
/// The grant of an application, **read again at every crossing** (D-10, D-11, D-53: off until the maintainer enables it): the highest world the maintainer enabled for it now. A grant that is withdrawn while a
/// problem is in flight stops it at its next crossing. Null: no power at all.
/// </summary>
public interface IGrantSource
{
    World? MaxWorldFor(string applicationId);
}

/// <summary>
/// The certificates that the approval authority withdrew, read like the stop: **a list that cannot be read counts as everything withdrawn**. A withdrawn certificate stops the problems that run on it
/// (the crossings, the acts and the sweep), not only the runners that read the list.
/// </summary>
public interface IApprovalWithdrawals
{
    bool IsWithdrawn(string certificateId);
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
    ValueTask<bool> RecordAsync(string eventName, string subject, string? detail, CancellationToken cancellationToken);
}

public static class MissionAuditEvents
{
    public const string ProblemStarted = "problem.started";
    public const string MachineRequalified = "machine.requalified";
    public const string PriorityRaised = "problem.priority-raised";
    public const string PrioritySet = "problem.priority-set";
    public const string ProblemHeld = "problem.held";
    public const string ProblemReleased = "problem.released";

    /// <summary>An act of a person that was written before it was made, and could not be made after all (the operation had left the line meanwhile).</summary>
    public const string ActNotMade = "problem.act-not-made";
    public const string ProblemQueued = "problem.queued";
    public const string QueuedProblemRefused = "problem.queue-refused";
    public const string ProblemParked = "problem.parked";
    public const string BotRebaseRequested = "problem.bot-rebase-requested";
    public const string ProblemFollowedBot = "problem.followed-bot";
    public const string LeadRefused = "lead.refused";
    public const string LeadPlanned = "lead.planned";
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
    public const string SweepFailed = "sweep.failed";
    public const string ActAuthorised = "act.authorised";
    public const string ActRefused = "act.refused";
    public const string ActPerformed = "act.performed";

    /// <summary>The code ended a mission because its runner refused to build (a conflict, a pull request that moved): no controller decided it, and the trail does not say so.</summary>
    public const string RunnerRefused = "mission.runner-refused";
}
