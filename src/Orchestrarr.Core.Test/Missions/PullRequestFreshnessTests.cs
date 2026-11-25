using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

public sealed class PullRequestFreshnessTests
{
    private const string Ticked = "1111111111111111111111111111111111111111";
    private const string Newer = "2222222222222222222222222222222222222222";
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);

    private static PlanEntry Entry(int? pullRequest = 7) =>
        new("p", "app", "x", "x", ["o/r"], World.Release, new MissionBudget(30, 3, 50), new Validation("alice", null), T0, pullRequest, Ticked);

    private static PullRequestFacts Facts(string head = Ticked, string? author = "dependabot[bot]", MergeState merge = MergeState.Clean, bool merged = false, bool closed = false) =>
        new(head, "base", false, ChecksState.Passing, ["package.json"], true, Merged: merged, Author: author, Merge: merge, Closed: closed);

    private static bool IsBot(string? login) => login == "dependabot[bot]";

    [Fact]
    public void A_pull_request_as_it_was_ticked_and_clean_is_ready()
    {
        PullRequestFreshness.Of(Entry(), Facts(), IsBot).Kind.Should().Be(Freshness.Ready);
    }

    [Theory]
    [InlineData(MergeState.Behind)]
    [InlineData(MergeState.Conflicting)]
    public void A_bots_pull_request_that_is_behind_or_in_conflict_needs_its_bot_to_rebase_it(MergeState merge)
    {
        var verdict = PullRequestFreshness.Of(Entry(), Facts(merge: merge), IsBot);

        verdict.Kind.Should().Be(Freshness.NeedsRebase);
        verdict.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_pull_request_of_someone_else_that_is_behind_is_left_to_the_proof_as_before()
    {
        PullRequestFreshness.Of(Entry(), Facts(author: "alice", merge: MergeState.Behind), IsBot).Kind.Should().Be(Freshness.Ready);
    }

    [Fact]
    public void A_new_head_made_by_the_bot_is_followed()
    {
        var verdict = PullRequestFreshness.Of(Entry(), Facts(head: Newer, merge: MergeState.Clean), IsBot);

        verdict.Kind.Should().Be(Freshness.Followed);
        verdict.Head.Should().Be(Newer);
    }

    [Fact]
    public void A_new_head_that_is_not_a_bots_is_never_followed()
    {
        PullRequestFreshness.Of(Entry(), Facts(head: Newer, author: "mallory"), IsBot).Kind.Should().Be(Freshness.Left);
        PullRequestFreshness.Of(Entry(), Facts(head: Newer, author: null), IsBot).Kind.Should().Be(Freshness.Left);
    }

    [Fact]
    public void A_pull_request_that_its_bot_closed_is_left_and_the_bot_is_not_asked_to_rebase_it()
    {
        PullRequestFreshness.Of(Entry(), Facts(merge: MergeState.Conflicting, closed: true), IsBot).Kind.Should().Be(Freshness.Left);
    }

    [Fact]
    public void A_merged_pull_request_is_left()
    {
        PullRequestFreshness.Of(Entry(), Facts(merged: true), IsBot).Kind.Should().Be(Freshness.Left);
    }

    [Fact]
    public void A_problem_with_no_pull_request_or_a_forge_that_does_not_answer_is_ready()
    {
        PullRequestFreshness.Of(Entry(pullRequest: null), Facts(merge: MergeState.Behind), IsBot).Kind.Should().Be(Freshness.Ready);
        PullRequestFreshness.Of(Entry(), null, IsBot).Kind.Should().Be(Freshness.Ready);
    }
}
