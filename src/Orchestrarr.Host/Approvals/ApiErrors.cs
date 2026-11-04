namespace Orchestrarr.Host.Approvals;

/// <summary>The codes of a refusal that is not the service's: who may call at all. The service's own are <c>ApprovalRefusal</c>.</summary>
internal static class ApiErrors
{
    public const string ApprovalsDisabled = "approvalsDisabled";
    public const string NotSignedIn = "notSignedIn";
    public const string NotAnOperator = "notAnOperator";
    public const string HostNotAllowed = "hostNotAllowed";
    public const string OriginNotAllowed = "originNotAllowed";
    public const string KillSwitchUnavailable = "killSwitchUnavailable";
    public const string AuditUnavailable = "auditUnavailable";
    public const string StorageUnavailable = "storageUnavailable";
}
