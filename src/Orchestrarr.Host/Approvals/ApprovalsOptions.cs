namespace Orchestrarr.Host.Approvals;

public sealed class ApprovalsOptions
{
    public const string SectionName = "Approvals";

    public ApprovalsMode Mode { get; set; } = ApprovalsMode.Disabled;

    /// <summary>The name the sandbox operator signs with.</summary>
    public string DevelopmentUser { get; set; } = "dev-operator";

    /// <summary>How many approvals the queue holds before it refuses (it never forgets one to make room). Unset: the default of where things are kept (<see cref="Orchestrarr.Core.Approvals.StoreLimits"/>).</summary>
    public int? QueueCapacity { get; set; }

    /// <summary>How many entries the audit trail holds (in memory or in the file) before it refuses. Unset: the default of where things are kept.</summary>
    public int? AuditCapacity { get; set; }

    /// <summary>Checked when the host starts: a capacity of zero or less would refuse every approval, or every entry of the trail.</summary>
    public void Validate()
    {
        if (QueueCapacity <= 0 || AuditCapacity <= 0)
        {
            throw new InvalidOperationException("Approvals:QueueCapacity and Approvals:AuditCapacity must be greater than zero.");
        }
        if (Mode == ApprovalsMode.Development && !Orchestrarr.Core.Launch.OrderValidation.IsValidSubject(DevelopmentUser))
        {
            throw new InvalidOperationException("Approvals:DevelopmentUser is not a valid name (letters, digits and . _ - @ : +).");
        }
    }
}
