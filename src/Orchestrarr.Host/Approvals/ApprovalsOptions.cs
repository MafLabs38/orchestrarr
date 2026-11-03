namespace Orchestrarr.Host.Approvals;

public sealed class ApprovalsOptions
{
    public const string SectionName = "Approvals";

    public ApprovalsMode Mode { get; set; } = ApprovalsMode.Disabled;

    /// <summary>The name the sandbox operator signs with.</summary>
    public string DevelopmentUser { get; set; } = "dev-operator";

    /// <summary>How many approvals the queue holds before it refuses (it never forgets one to make room).</summary>
    public int QueueCapacity { get; set; } = 500;

    /// <summary>How many entries the in-memory audit trail holds before it refuses.</summary>
    public int AuditCapacity { get; set; } = 2000;

    /// <summary>Checked when the host starts: a capacity of zero or less would refuse every approval, or every entry of the trail.</summary>
    public void Validate()
    {
        if (QueueCapacity <= 0 || AuditCapacity <= 0)
        {
            throw new InvalidOperationException("Approvals:QueueCapacity and Approvals:AuditCapacity must be greater than zero.");
        }
    }
}
