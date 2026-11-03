namespace Orchestrarr.Host.Approvals;

public enum ApprovalsMode
{
    /// <summary>Nobody can approve: the interface shows the cards and says so.</summary>
    Disabled,

    /// <summary>A single, fixed operator with no sign-in, for the local sandbox only: the host refuses to start with it outside Development.</summary>
    Development,
}

public sealed class ApprovalsOptions
{
    public const string SectionName = "Approvals";

    public ApprovalsMode Mode { get; set; } = ApprovalsMode.Disabled;

    /// <summary>The name the sandbox operator signs with.</summary>
    public string DevelopmentUser { get; set; } = "dev-operator";
}

/// <summary>The powers of the configuration: <c>Powers:Grants</c>, a list of repository and steps. Empty: nothing is granted.</summary>
public sealed class PowersOptions
{
    public const string SectionName = "Powers";

    public List<PowerGrantOptions> Grants { get; set; } = [];
}

public sealed class PowerGrantOptions
{
    public string Repository { get; set; } = string.Empty;

    public List<Orchestrarr.Core.WorkItems.StepKind> Steps { get; set; } = [];
}
