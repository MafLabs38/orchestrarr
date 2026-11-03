using Orchestrarr.Core.Forge;

namespace Orchestrarr.Core.Test;

public sealed class PullRequestInfoTests
{
    private static PullRequestInfo PullRequest(bool draft = false, ChecksState checks = ChecksState.Passing, MergeState merge = MergeState.Clean) => new(
        1, "t", new Uri("https://forge.example/o/r/pull/1"), "bot", "b", draft, checks, merge, [], [], default, new string('a', 40));

    [Fact]
    public void A_pull_request_is_ready_to_merge_when_its_checks_pass_and_the_forge_says_clean()
    {
        PullRequest().ReadyToMerge.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, ChecksState.Passing, MergeState.Clean)]
    [InlineData(false, ChecksState.Failing, MergeState.Clean)]
    [InlineData(false, ChecksState.Pending, MergeState.Clean)]
    [InlineData(false, ChecksState.Unknown, MergeState.Clean)]
    [InlineData(false, ChecksState.Passing, MergeState.Unstable)]
    [InlineData(false, ChecksState.Passing, MergeState.Conflicting)]
    [InlineData(false, ChecksState.Passing, MergeState.Behind)]
    [InlineData(false, ChecksState.Passing, MergeState.Blocked)]
    [InlineData(false, ChecksState.Passing, MergeState.Unknown)]
    public void Anything_else_is_not_ready_whatever_is_right_about_the_rest(bool draft, ChecksState checks, MergeState merge)
    {
        PullRequest(draft, checks, merge).ReadyToMerge.Should().BeFalse();
    }
}
