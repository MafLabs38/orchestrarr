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

    private static ProblemRun Run(string rowId, ProblemState state) =>
        new(PlanEntryFactory.ProblemIdOf(rowId), "shop", state, T0, Entry(rowId), World.Release, true, new SignedApproval(new ApprovalCertificate("c", CertificateKind.Problem, "p", "shop", ["o/shop"], World.Release, new MissionBudget(1, 1, 1), T0, T0.AddHours(1), "x", "n", [], 8), []));

    private static ServiceProvider Services(IProblemStore? problems, ProblemQueue? queue = null, TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(time ?? new Microsoft.Extensions.Time.Testing.FakeTimeProvider(T0.AddHours(1)));
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
    public void A_decision_done_lately_is_a_decision_taken_and_one_put_aside_is_one_to_take_again()
    {
        var store = new InMemoryProblemStore();
        store.Save(Run("row-done", ProblemState.Closed));
        store.Save(Run("row-aside", ProblemState.Failed));
        using var lately = Services(store);
        using var later = Services(store, time: new Microsoft.Extensions.Time.Testing.FakeTimeProvider(T0.AddHours(7)));

        var rows = ProblemsEndpoints.MarkWorkedOn([Row("row-done"), Row("row-aside")], lately);

        rows.Select(row => (row.Id, row.RecentlyDone, row.InProgress)).Should().Equal(("row-done", true, false), ("row-aside", false, false));
        ProblemsEndpoints.MarkWorkedOn([Row("row-done")], later).Single().RecentlyDone.Should().BeFalse("a finding the fleet still reports hours after it was done is a decision to take again");
    }

    [Fact]
    public void A_host_without_missions_leaves_the_table_as_it_is()
    {
        using var services = Services(null);
        IReadOnlyList<ProblemRow> table = [Row("row-1")];

        ProblemsEndpoints.MarkWorkedOn(table, services).Should().BeSameAs(table);
    }
}
