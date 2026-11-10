namespace Orchestrarr.Host.Approvals;

public sealed class ApprovalsOptions
{
    public const string SectionName = "Approvals";

    /// <summary>How many times a person (or, with no one signed in, an address) may try to approve in a window (a minute by default): more is a 429, before anything is recorded. Refusals are traced and the trail is bounded, so a loop must not fill it. The global stop is never limited.</summary>
    public int ApprovalsLimit { get; set; } = 30;

    /// <summary>The window of that limit, in minutes.</summary>
    public int ApprovalsWindowMinutes { get; set; } = 1;

    /// <summary>How many approvals the queue holds before it refuses (it never forgets one to make room). Unset: the default of where things are kept (<see cref="Orchestrarr.Core.Approvals.StoreLimits"/>).</summary>
    public int? QueueCapacity { get; set; }

    /// <summary>How many entries the audit trail holds (in memory or in the file) before it refuses. Unset: the default of where things are kept.</summary>
    public int? AuditCapacity { get; set; }

    /// <summary>Checked when the host starts: a capacity of zero or less would refuse every approval, or every entry of the trail, and a trail of one place is the stop's alone.</summary>
    public void Validate()
    {
        if (QueueCapacity <= 0 || AuditCapacity <= 0)
        {
            throw new InvalidOperationException("Approvals:QueueCapacity and Approvals:AuditCapacity must be greater than zero.");
        }
        if (ApprovalsLimit <= 0 || ApprovalsWindowMinutes <= 0)
        {
            throw new InvalidOperationException("Approvals:ApprovalsLimit and Approvals:ApprovalsWindowMinutes must be greater than zero.");
        }
        if (AuditCapacity < 2)
        {
            // One place is the reserve of the global stop: nothing else could ever be traced, so nothing could be approved
            throw new InvalidOperationException("Approvals:AuditCapacity must be at least 2: the last place is kept for the global stop.");
        }
    }
}
