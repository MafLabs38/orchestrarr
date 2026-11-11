using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Test.Decisions;

public sealed class DecisionSelectionTests
{
    private static readonly string Head = new('a', 40);

    private static ProblemRow Row(string id = "pr:example/shop#1", Ceiling goesUpTo = Ceiling.Production, int? pullRequest = 1, string? head = null, bool canStop = true, bool canRepair = false) =>
        new(id, "repository:example/shop", "example/shop", ProblemKind.DependencyUpdate, "lodash 1.0.0 → 1.1.0", null, null, UpdateKind.Minor, ChecksState.Passing, null, pullRequest,
            pullRequest is null ? null : head ?? Head, goesUpTo, CeilingReason.None, canStop, canRepair);

    private static Dictionary<string, ProblemRow> Table(params ProblemRow[] rows) => rows.ToDictionary(row => row.Id, StringComparer.Ordinal);

    private static ChosenRow Chose(ProblemRow row, bool stop = false, Ceiling? goesUpTo = null, string? head = null, bool repair = false) => new(row.Id, head ?? row.HeadSha, goesUpTo ?? row.GoesUpTo, stop, repair);

    [Fact]
    public void What_the_operator_saw_becomes_the_problems_to_approve_with_the_steps_of_the_ceiling_and_the_plan_in_a_note()
    {
        var qualify = Row("pr:example/shop#2", Ceiling.Qualification, 2, new string('b', 40), canStop: false);
        var production = Row();

        var resolution = DecisionSelection.Resolve(Table(production, qualify), [Chose(production, stop: true), Chose(qualify, stop: true)], "abc123", "abc123");

        resolution.Refused.Should().BeFalse();
        resolution.Selection.Select(problem => (problem.PullRequest, problem.Steps.Count)).Should().Equal((1, 2), (2, 1));
        resolution.Selection[0].Note.Should().Be("goesUpTo=Production stopBeforeProduction=True repair=False descriptions=abc123");
        resolution.Selection[1].Note.Should().Contain("stopBeforeProduction=False", "a row that cannot reach production has nothing to stop");
    }

    [Fact]
    public void A_row_chosen_to_be_repaired_stands_for_the_repair_alone_and_says_so_in_the_note()
    {
        var failing = Row("pr:example/shop#3", Ceiling.Qualification, 3, canStop: false, canRepair: true);

        var resolution = DecisionSelection.Resolve(Table(failing), [Chose(failing, repair: true)], "abc", "abc");

        resolution.Refused.Should().BeFalse();
        resolution.Selection.Single().Steps.Should().Equal(StepKind.Repair);
        resolution.Selection.Single().Note.Should().Contain("repair=True");
    }

    [Fact]
    public void A_repair_that_the_row_does_not_offer_refuses_the_selection()
    {
        var passing = Row("pr:example/shop#4", Ceiling.Qualification, 4, canStop: false, canRepair: false);

        var resolution = DecisionSelection.Resolve(Table(passing), [Chose(passing, repair: true)], "abc", "abc");

        resolution.Refused.Should().BeTrue();
        resolution.Mismatch.Should().Be(passing);
    }

    [Fact]
    public void No_descriptions_are_noted_as_none()
    {
        DecisionSelection.Resolve(Table(Row()), [Chose(Row())], string.Empty, string.Empty).Selection.Single().Note.Should().EndWith("descriptions=none");
    }

    public static TheoryData<string> Moved() => ["gone", "noAction", "head", "higher", "lower", "unsaid", "noPullRequest", "descriptions"];

    [Theory]
    [MemberData(nameof(Moved))]
    public void Anything_that_moved_since_the_operator_looked_refuses_the_whole_selection(string what)
    {
        var good = Row("pr:example/shop#2", Ceiling.Qualification, 2, new string('b', 40), canStop: false);
        var row = Row();
        var table = Table(row, good);
        ChosenRow chosen = what switch
        {
            "gone" => new ChosenRow("pr:example/shop#99", Head, Ceiling.Production, false),
            "noAction" => Chose(row),
            "head" => Chose(row, head: new string('f', 40)),
            "higher" => Chose(row, goesUpTo: Ceiling.Qualification),
            "lower" => Chose(good, goesUpTo: Ceiling.Production),
            "unsaid" => new ChosenRow(row.Id, row.HeadSha, null, false),
            "noPullRequest" => Chose(row),
            _ => Chose(row),
        };
        if (what == "noAction")
        {
            table = Table(Row(goesUpTo: Ceiling.NoAction), good);
        }
        if (what == "noPullRequest")
        {
            table = Table(Row(pullRequest: null), good);
        }

        var resolution = DecisionSelection.Resolve(table, [Chose(good), chosen], what == "descriptions" ? "old" : "abc", "abc");

        resolution.Refused.Should().BeTrue();
        resolution.Selection.Should().BeEmpty("all or nothing");
    }

    [Fact]
    public void An_empty_selection_resolves_to_nothing_and_is_left_to_the_service_to_refuse()
    {
        var resolution = DecisionSelection.Resolve(Table(Row()), [], "abc", "abc");

        resolution.Refused.Should().BeFalse();
        resolution.Selection.Should().BeEmpty();
    }
}
