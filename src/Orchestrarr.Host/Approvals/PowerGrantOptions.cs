using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Host.Approvals;

public sealed class PowerGrantOptions
{
    public string Repository { get; set; } = string.Empty;

    public List<StepKind> Steps { get; set; } = [];
}
