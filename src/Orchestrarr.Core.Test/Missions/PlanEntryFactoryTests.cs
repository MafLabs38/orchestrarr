using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Missions;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Test.Missions;

public sealed class PlanEntryFactoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static ProblemRow Row(string id = "pullrequests:repository:example/shop#3", string? source = "example/shop", int? pullRequest = 3, string? head = "abc123") => new(
        id, "repository:example/shop", "example/shop", ProblemKind.DependencyUpdate, "minimist 0.0.8 → 1.2.8", null, null, UpdateKind.Major, ChecksState.Passing, null, pullRequest, head,
        Ceiling.NoAction, CeilingReason.None, false, Source: source);

    [Fact]
    public void A_ticked_pull_request_becomes_a_plan_entry_that_carries_the_pull_request_and_the_head_the_maintainer_saw()
    {
        var entry = PlanEntryFactory.From(Row(), "alice", Now, World.Release, new MissionBudget(20, 3, 50));

        entry.Should().NotBeNull();
        entry.Repositories.Should().Equal("example/shop");
        entry.ApplicationId.Should().Be("shop");
        entry.PullRequest.Should().Be(3);
        entry.HeadSha.Should().Be("abc123");
        entry.MaxWorld.Should().Be(World.Release);
        entry.Validation.By.Should().Be("alice");
        entry.Objective.Should().Contain("#3").And.Contain("example/shop");
    }

    [Fact]
    public void The_identifier_of_a_problem_is_stable_for_a_row_and_plain_text()
    {
        PlanEntryFactory.ProblemIdOf("a:b/c#1").Should().Be(PlanEntryFactory.ProblemIdOf("a:b/c#1"));
        PlanEntryFactory.ProblemIdOf("a:b/c#1").Should().NotBe(PlanEntryFactory.ProblemIdOf("a:b/c#2"));
        PlanEntryFactory.ProblemIdOf("a:b/c#1").Should().MatchRegex("^p-[0-9a-f]{12}$");
    }

    [Theory]
    [InlineData("not a repository")]
    [InlineData("")]
    public void A_row_that_names_no_repository_is_no_problem_that_can_be_started(string source)
    {
        PlanEntryFactory.From(Row(source: source) with { Application = source }, "alice", Now, World.Release, new MissionBudget(20, 3, 50)).Should().BeNull();
    }
}
