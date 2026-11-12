using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.WorkItems;
using Orchestrarr.Host.Decisions;

namespace Orchestrarr.Host.Test;

/// <summary>Which rows are shown once the skips are applied (ADR 0027).</summary>
public sealed class VisibleRowsTests
{
    private static ProblemRow Alert(Severity severity) =>
        new("finding:repository:example/api:DependencyAlerts", "repository:example/api", "example/api", ProblemKind.DependencyAlert, "3", severity, 3, null, null, null, null, null,
            Ceiling.NoAction, CeilingReason.NoActionDefined, false);

    [Fact]
    public void A_skip_made_when_a_row_was_less_serious_lapses_when_the_row_is_a_danger()
    {
        var history = new InMemoryProblemHistory();
        history.Record(new HistoryEntry(0, "finding:repository:example/api:DependencyAlerts", "3", HistoryState.Skipped, "example/api", ProblemKind.DependencyAlert, "3", null, null, null, null, null, DateTimeOffset.UnixEpoch, "alice", null));

        ProblemsEndpoints.Visible([Alert(Severity.Warning)], history, new InMemoryApprovalStore()).Should().BeEmpty("the skip holds while the row is as serious as it was");
        ProblemsEndpoints.Visible([Alert(Severity.Danger)], history, new InMemoryApprovalStore()).Should().ContainSingle("a danger is never hidden").Which.Tab.Should().Be(ProblemTab.Act);
    }
}
