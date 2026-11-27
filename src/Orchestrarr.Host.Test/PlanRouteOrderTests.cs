using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Forge;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

/// <summary>A selection is planned and started in the order of the route (ADR 0055), not in the order of the ticks.</summary>
public sealed class PlanRouteOrderTests
{
    private static ProblemRow Row(string id, ProblemKind kind, ProblemNature nature) =>
        new(id, "w", "shop", kind, id, null, null, null, null, null, null, null, Ceiling.Production, CeilingReason.None, true, Nature: nature);

    [Fact]
    public void The_rows_of_a_selection_are_taken_red_pipeline_first_then_the_runtime_the_alerts_and_the_updates()
    {
        var table = new[]
        {
            Row("bump", ProblemKind.DependencyUpdate, ProblemNature.LibraryUpdate),
            Row("alert", ProblemKind.DependencyAlert, ProblemNature.SecurityAlert),
            Row("node", ProblemKind.DependencyUpdate, ProblemNature.BaseImageUpdate),
            Row("red", ProblemKind.FailingCi, ProblemNature.FailingPipeline),
            Row("bump-2", ProblemKind.DependencyUpdate, ProblemNature.LibraryUpdate),
        }.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var ticked = new[] { "bump", "unknown", "alert", "bump-2", "node", "red", "bump" }.Select(id => new ValidatedRow(id, null));

        PlanEndpoints.InRouteOrder(ticked, table).Select(row => row.Id).Should().Equal("red", "node", "alert", "bump", "bump-2", "unknown");
    }
}
