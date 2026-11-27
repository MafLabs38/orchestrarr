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

    private static PullRequestFacts Facts(string head = Ticked, string? author = "dependabot[bot]", MergeState merge = MergeState.Clean, bool merged = false, bool closed = false, int? behindBy = null, IReadOnlyList<string>? commits = null) =>
        new(head, "base", false, ChecksState.Passing, ["package.json"], true, Merged: merged, Author: author, Merge: merge, Closed: closed, BehindBy: behindBy, CommitAuthors: commits ?? (author is null ? null : [author]), HeadPushedBy: author);

    private static bool IsBot(string? login) => login == "dependabot[bot]";

    [Theory]
    [InlineData("orchestrarr-agent")]
    [InlineData("")]
    public void A_new_head_with_a_commit_that_is_not_the_bots_own_is_left_to_be_ticked_again(string other)
    {
        var verdict = PullRequestFreshness.Of(Entry(), Facts(head: "new", commits: ["dependabot[bot]", other]), IsBot);

        verdict.Kind.Should().Be(Freshness.Left, "the author of a pull request stays the bot whoever pushes to its branch: only the bot's own commits are followed");
        verdict.Reason.Should().Contain("someone other than its bot");
    }

    [Theory]
    [InlineData("orchestrarr-sandbox-agent[bot]")]
    [InlineData(null)]
    public void A_new_head_that_the_bot_did_not_push_itself_is_left_even_when_its_commits_claim_to_be_the_bots(string? pusher)
    {
        var claimed = Facts(head: "new") with { HeadPushedBy = pusher };

        PullRequestFreshness.Of(Entry(), claimed, IsBot).Kind.Should().Be(Freshness.Left, "the account of a commit, and even a signature the forge made through its API, can be made to look like the bot's; the account that pushed cannot");
        PullRequestFreshness.Of(Entry(), claimed with { HeadPushedBy = "Dependabot[bot]" }, IsBot).Kind.Should().Be(Freshness.Followed, "the bot's own push is followed, whatever the case of its name");
    }

    [Fact]
    public void A_new_head_whose_commits_cannot_be_read_is_not_followed()
    {
        var facts = Facts(head: "new") with { CommitAuthors = null };

        PullRequestFreshness.Of(Entry(), facts, IsBot).Kind.Should().Be(Freshness.Left);
        PullRequestFreshness.IsTheBotsOwn(facts with { CommitAuthors = [] }).Should().BeFalse();
    }

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
    public void A_head_that_lacks_commits_of_the_base_is_behind_even_when_the_forge_has_not_worked_out_its_merge_state_yet()
    {
        PullRequestFreshness.Of(Entry(), Facts(merge: MergeState.Unknown, behindBy: 2), IsBot).Kind.Should().Be(Freshness.NeedsRebase);
        PullRequestFreshness.Of(Entry(), Facts(merge: MergeState.Unknown, behindBy: 0), IsBot).Kind.Should().Be(Freshness.Ready);
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
    public void A_new_head_that_the_bot_made_on_a_base_that_has_moved_since_is_followed_but_waits_for_another_rebase()
    {
        var verdict = PullRequestFreshness.Of(Entry(), Facts(head: Newer, merge: MergeState.Unknown, behindBy: 1), IsBot);

        verdict.Kind.Should().Be(Freshness.NeedsRebase);
        verdict.Head.Should().Be(Newer);
    }

    [Fact]
    public void A_new_head_that_is_not_a_bots_is_never_followed()
    {
        PullRequestFreshness.Of(Entry(), Facts(head: Newer, author: "mallory"), IsBot).Kind.Should().Be(Freshness.Left);
        PullRequestFreshness.Of(Entry(), Facts(head: Newer, author: null), IsBot).Kind.Should().Be(Freshness.Left);
    }

    [Fact]
    public void A_pull_request_that_its_bot_closed_is_done_by_another_change_and_one_that_someone_else_closed_is_left()
    {
        PullRequestFreshness.Of(Entry(), Facts(merge: MergeState.Conflicting, closed: true), IsBot).Kind.Should().Be(Freshness.Superseded);
        PullRequestFreshness.Of(Entry(), Facts(author: "alice", closed: true), IsBot).Kind.Should().Be(Freshness.Left);
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
