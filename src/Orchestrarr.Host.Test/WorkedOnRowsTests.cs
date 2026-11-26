using Microsoft.Extensions.DependencyInjection;
using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Missions;
using Orchestrarr.Core.WorkItems;
using Orchestrarr.Host.Decisions;

namespace Orchestrarr.Host.Test;

public sealed class WorkedOnRowsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);

    private static ProblemRow Row(string id) =>
        new(id, "repository:o/shop", "o/shop", ProblemKind.ImageUpdate, "nginx:1.14 → 1.28", Severity.Info, 1, null, null, null, null, null, Ceiling.Production, CeilingReason.None, false);

    private static PlanEntry Entry(string rowId) =>
        new(PlanEntryFactory.ProblemIdOf(rowId), "shop", "ImageUpdate", "o", ["o/shop"], World.Release, new MissionBudget(1, 1, 1), new Validation("x", null), T0);

    private static ProblemRun Run(string rowId, ProblemState state, DateTimeOffset? endedAt = null) =>
        new(PlanEntryFactory.ProblemIdOf(rowId), "shop", state, T0, Entry(rowId), World.Release, true, new SignedApproval(new ApprovalCertificate("c", CertificateKind.Problem, "p", "shop", ["o/shop"], World.Release, new MissionBudget(1, 1, 1), T0, T0.AddHours(1), "x", "n", [], 8), []), EndedAt: endedAt);

    private static ServiceProvider Services(IProblemStore? problems, ProblemQueue? queue = null)
    {
        var services = new ServiceCollection();
        if (problems is not null)
        {
            services.AddSingleton(problems);
        }
        if (queue is not null)
        {
            services.AddSingleton(queue);
        }
        return services.BuildServiceProvider();
    }

    [Fact]
    public void A_row_whose_problem_is_in_progress_or_waits_in_the_line_reads_in_progress_and_the_others_do_not()
    {
        var store = new InMemoryProblemStore();
        store.Save(Run("row-working", ProblemState.InProgress));
        store.Save(Run("row-closed", ProblemState.Closed));
        var queue = new ProblemQueue();
        queue.Enqueue(new QueuedProblem("row-waiting", Entry("row-waiting"), T0));
        using var services = Services(store, queue);

        var rows = ProblemsEndpoints.MarkWorkedOn([Row("row-working"), Row("row-waiting"), Row("row-closed"), Row("row-free")], services);

        rows.Select(row => (row.Id, row.InProgress)).Should().Equal(("row-working", true), ("row-waiting", true), ("row-closed", false), ("row-free", false));
    }

    [Fact]
    public void A_decision_done_is_taken_until_the_fleet_is_read_again_and_one_put_aside_is_one_to_take_again()
    {
        var store = new InMemoryProblemStore();
        store.Save(Run("row-done", ProblemState.Closed, endedAt: T0.AddMinutes(30)));
        store.Save(Run("row-aside", ProblemState.Failed));
        using var services = Services(store);

        var before = ProblemsEndpoints.MarkWorkedOn([Row("row-done"), Row("row-aside")], services, fleetReadAt: T0.AddMinutes(20));

        before.Select(row => (row.Id, row.RecentlyDone, row.InProgress)).Should().Equal(("row-done", true, false), ("row-aside", false, false));
        ProblemsEndpoints.MarkWorkedOn([Row("row-done")], services, fleetReadAt: T0.AddMinutes(40)).Single().RecentlyDone.Should().BeFalse("a read of the fleet made after it was done that still reports it says it is there again");
    }

    [Fact]
    public void A_host_without_missions_leaves_the_table_as_it_is()
    {
        using var services = Services(null);
        IReadOnlyList<ProblemRow> table = [Row("row-1")];

        ProblemsEndpoints.MarkWorkedOn(table, services).Should().BeSameAs(table);
    }
}
