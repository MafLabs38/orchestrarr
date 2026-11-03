namespace Orchestrarr.Core.Approvals;

/// <summary>The events of the audit trail that approving and the global stop write.</summary>
public static class ApprovalAuditEvents
{
    public const string Recorded = "approval.recorded";
    public const string Refused = "approval.refused";
    public const string KillSwitchEngaged = "killswitch.engaged";
    public const string KillSwitchReleased = "killswitch.released";
}
