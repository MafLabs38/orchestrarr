using Orchestrarr.Core.Infrastructure;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Host.Decisions;

/// <summary>What the status says about the code of the scope: read from what was read last, never by reading anything (ADR 0028).</summary>
internal static class CodeStatusReader
{
    public static CodeStatus Of(InfrastructureCatalog infrastructure, WorkItemReport? report)
    {
        var code = infrastructure.Peek();
        var warnings = report is null ? [] : ScopeChecks.UnknownEnvironments(infrastructure.Scope, report.Items.SelectMany(item => item.Placements).Select(placement => placement.EnvironmentId));
        return new CodeStatus(code.Index.Problems, warnings, code.Error);
    }
}
